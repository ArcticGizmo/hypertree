using Hypertree.Ipc;
using Hypertree.Status;

namespace Hypertree.Cli;

/// <summary>
/// The three collaborators <see cref="Commands"/> touches the outside world through — the published status,
/// the tray control pipe, and console output — as seams. The commands were static-on-static
/// (<c>StatusFile.Read</c> / <c>ControlClient.Send</c> / <c>Output</c>), so their formatting and exit-code
/// logic could only be exercised by spawning a real process; behind these interfaces it's unit-testable with
/// a canned status, a fake transport, and a recording writer, while the production wiring (below) is an
/// obvious one-liner each.
/// </summary>
internal interface IStatusSource
{
    /// <summary>The published status, or null when no tray is running / it's unreadable.</summary>
    StatusSnapshot? Read();
}

/// <summary>Sends one request to the running tray and returns its reply (never throws — see
/// <see cref="ControlClient"/>).</summary>
internal interface IControlTransport
{
    ControlResponse Send(ControlRequest request);
}

/// <summary>Where a command's output goes. <see cref="Paint"/> is the colour wrap (a no-op off a terminal),
/// kept on the sink so a test can strip it and assert on plain text.</summary>
internal interface ICliOutput
{
    /// <summary>A result line — stdout.</summary>
    void Line(string text = "");
    /// <summary>A diagnostic — stderr, prefixed.</summary>
    void Error(string text);
    /// <summary>Wrap <paramref name="text"/> in a colour escape, or return it untouched when colour is off.</summary>
    string Paint(string text, string colour);
}

/// <summary>Production status source: the shared <see cref="StatusFile"/>.</summary>
internal sealed class StatusFileSource : IStatusSource
{
    public StatusSnapshot? Read() => StatusFile.Read();
}

/// <summary>Production transport: the real control pipe.</summary>
internal sealed class ControlClientTransport : IControlTransport
{
    public ControlResponse Send(ControlRequest request) => ControlClient.Send(request);
}

/// <summary>Production output: the console conventions in <see cref="Output"/>.</summary>
internal sealed class ConsoleOutput : ICliOutput
{
    public void Line(string text = "") => Output.Line(text);
    public void Error(string text) => Output.Error(text);
    public string Paint(string text, string colour) => Output.Paint(text, colour);
}
