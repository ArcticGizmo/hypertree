using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Hypertree.Ipc;

namespace Hypertree.App.Ipc;

/// <summary>
/// Listens on Hypertree's control pipe and hands each request to the app to carry out. This is the only
/// way in from outside the process: <c>htree goto …</c> can't drive virtual desktops itself (the
/// single-instance guard exists precisely to stop a second process trying), so it asks the tray, and the
/// tray answers here.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> The accept loop runs on a background thread — a blocking wait on the UI thread
/// would freeze the tray — but the handler is marshalled onto the UI thread before it touches anything,
/// because the desktop COM RCWs are apartment-bound to it and the navigation model is not thread-safe.
/// The background thread then blocks on that result, which is fine: it exists only to serve this one
/// connection.</para>
///
/// <para><b>Security.</b> The pipe name carries the logon session id (see
/// <see cref="ControlProtocol.PipeName"/>) so sessions can't collide or reach each other. Within a session
/// the default pipe ACL applies, which is the right boundary: anything running as the same user in the
/// same session can already synthesise the hotkeys this pipe stands in for, so locking it down further
/// would buy nothing.</para>
///
/// <para>One connection per request, no keep-alive. A control call is rare and cheap, and a stateless
/// server has nothing to leak or resynchronise if a client dies mid-call.</para>
/// </remarks>
internal sealed class ControlServer : IDisposable
{
    private readonly Func<ControlRequest, ControlResponse> _handle;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    /// <param name="handle">Carries out a request. Always invoked on the UI thread.</param>
    public ControlServer(Func<ControlRequest, ControlResponse> handle) => _handle = handle;

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new Thread(Listen) { IsBackground = true, Name = "hypertree-control" };
        _listener.Start();
    }

    private void Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    ControlProtocol.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                Serve(pipe);
            }
            catch (OperationCanceledException) { return; } // Dispose — shutting down
            catch
            {
                // A client that died mid-call, or a transient pipe error. Never let one bad connection end
                // the loop, or the CLI stops working for the rest of the tray's life. Pause briefly so a
                // persistent failure (e.g. the name is taken) can't spin the CPU.
                if (_stop.Token.WaitHandle.WaitOne(250)) return;
            }
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        string line = ReadLine(pipe);
        if (string.IsNullOrWhiteSpace(line)) return;

        ControlResponse response;
        try
        {
            var request = JsonSerializer.Deserialize(line, ControlProtocol.RequestInfo);
            response = request is null
                ? ControlResponse.Failure(ExitCode.BadUsage, "Unreadable request.")
                // Hop to the UI thread: the handler touches the desktop COM and the navigation model,
                // neither of which may be used from here.
                : Dispatcher.UIThread.InvokeAsync(() => Invoke(request)).GetAwaiter().GetResult();
        }
        catch (JsonException)
        {
            response = ControlResponse.Failure(ExitCode.BadUsage, "Malformed request.");
        }
        catch (Exception ex)
        {
            response = ControlResponse.Failure(ExitCode.Failed, ex.Message);
        }

        try
        {
            byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, ControlProtocol.ResponseInfo) + "\n");
            pipe.Write(reply);
            pipe.Flush();
            // Let the client finish reading before the using-block tears the pipe down under it.
            pipe.WaitForPipeDrain();
        }
        catch { /* client hung up before reading — nothing to report to */ }
    }

    // Run the handler, converting a throw into a failure response rather than letting it escape onto the
    // UI thread, where it would take the tray down.
    private ControlResponse Invoke(ControlRequest request)
    {
        try { return _handle(request); }
        catch (Exception ex) { return ControlResponse.Failure(ExitCode.Failed, ex.Message); }
    }

    private string ReadLine(NamedPipeServerStream pipe)
    {
        var buffer = new byte[1024];
        var sb = new StringBuilder();
        while (true)
        {
            int n;
            try { n = pipe.ReadAsync(buffer.AsMemory(), _stop.Token).AsTask().GetAwaiter().GetResult(); }
            catch { return ""; }
            if (n == 0) return sb.ToString(); // client hung up
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            int nl = sb.ToString().IndexOf('\n');
            if (nl >= 0) return sb.ToString(0, nl);
            if (sb.Length > 64 * 1024) return ""; // no newline in a sane request — refuse to buffer forever
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // Cancelling unblocks WaitForConnectionAsync; the join is a courtesy so the pipe is torn down before
        // the status file says we've gone. Never wait long — shutdown must not hang on a wedged client.
        try { _listener?.Join(TimeSpan.FromMilliseconds(500)); } catch { /* best-effort */ }
        _stop.Dispose();
    }
}
