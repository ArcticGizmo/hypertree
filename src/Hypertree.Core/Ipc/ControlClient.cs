using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Hypertree.Ipc;

/// <summary>
/// Sends one request to the running tray over the control pipe and returns its reply. Used by
/// <c>htree</c>; kept here beside <see cref="ControlProtocol"/> so the wire format has exactly one
/// implementation on each side of it.
/// </summary>
/// <remarks>
/// <para>The framing is one line of JSON each way, in byte mode. Message-mode pipes would frame it for us,
/// but only if both ends agree to it and the reader checks <c>IsMessageComplete</c> correctly — a newline
/// is the same guarantee with nothing to get subtly wrong, and it stays readable when poking at the pipe
/// by hand.</para>
///
/// <para>Never throws. Every failure — no tray listening, a tray that hangs, a garbled reply — comes back
/// as a <see cref="ControlResponse"/> carrying the <see cref="ExitCode"/> the caller should exit with,
/// because the caller is a command-line process whose whole job is to turn this into an exit code and a
/// line on stderr.</para>
/// </remarks>
public static class ControlClient
{
    public static ControlResponse Send(ControlRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", ControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try { pipe.Connect((int)ControlProtocol.ConnectTimeout.TotalMilliseconds); }
            // Timeout: nothing is listening. IOException: the pipe exists but the tray dropped us (it is
            // shutting down). Either way there is no tray to serve this, which is the same answer.
            catch (TimeoutException) { return NoTray(); }
            catch (IOException) { return NoTray(); }

            string line = JsonSerializer.Serialize(request, ControlProtocol.RequestInfo) + "\n";
            pipe.Write(Encoding.UTF8.GetBytes(line));
            pipe.Flush();

            string reply = ReadLine(pipe);
            if (string.IsNullOrWhiteSpace(reply))
                return ControlResponse.Failure(ExitCode.Failed, "The tray closed the connection without replying.");

            return JsonSerializer.Deserialize(reply, ControlProtocol.ResponseInfo)
                   ?? ControlResponse.Failure(ExitCode.Failed, "The tray sent an unreadable reply.");
        }
        catch (Exception ex)
        {
            return ControlResponse.Failure(ExitCode.Failed, ex.Message);
        }
    }

    private static ControlResponse NoTray()
        => ControlResponse.Failure(ExitCode.NoTray, "No Hypertree tray is running.");

    // Read up to the first newline (or end of stream). Async so the timeout can actually interrupt a
    // wedged tray — a synchronous pipe read ignores cancellation and would hang the caller's shell.
    private static string ReadLine(NamedPipeClientStream pipe)
    {
        using var cts = new CancellationTokenSource(ControlProtocol.ReplyTimeout);
        var buffer = new byte[1024];
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                int n = pipe.ReadAsync(buffer.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0) break; // tray closed the connection
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                int nl = sb.ToString().IndexOf('\n');
                if (nl >= 0) return sb.ToString(0, nl);
                if (sb.Length > 64 * 1024) return ""; // no newline in a sane reply — refuse to buffer forever (mirrors the server)
            }
        }
        catch (OperationCanceledException) { return ""; }
        return sb.ToString();
    }
}
