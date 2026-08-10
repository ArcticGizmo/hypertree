using Hypertree.Cli;
using Hypertree.Ipc;
using Hypertree.Status;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Unit tests for the CLI commands, now that they take their status source, control transport and output as
/// seams (<see cref="IStatusSource"/> / <see cref="IControlTransport"/> / <see cref="ICliOutput"/>) instead
/// of static-on-static calls. Exercises the formatting and exit-code logic in-process — the coverage the
/// process-spawning end-to-end tests couldn't give cheaply.
/// </summary>
public sealed class CliCommandsTests
{
    private sealed class FakeStatus(StatusSnapshot? snapshot) : IStatusSource
    {
        public StatusSnapshot? Read() => snapshot;
    }

    private sealed class FakeTransport : IControlTransport
    {
        public ControlRequest? Sent;
        public ControlResponse Response = ControlResponse.Success("landed");
        public ControlResponse Send(ControlRequest request) { Sent = request; return Response; }
    }

    private sealed class RecordingOutput : ICliOutput
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Errors = new();
        public void Line(string text = "") => Lines.Add(text);
        public void Error(string text) => Errors.Add(text);
        public string Paint(string text, string colour) => text; // strip colour so tests assert on plain text
    }

    private static StatusSnapshot TwoRows() => new()
    {
        Rows =
        {
            new StatusRow { Kind = RowKind.Main, Name = "main", Cursor = 0, Desktops = { new StatusDesktop { Label = "one" } } },
            new StatusRow
            {
                Kind = RowKind.Branch, Id = Guid.NewGuid(), Name = "feat", Cursor = 1,
                Desktops = { new StatusDesktop { Label = "a" }, new StatusDesktop { Label = "b" } },
            },
        },
        Current = new StatusPosition { Row = 1, Desktop = 1 }, // on feat/b
    };

    private static (Commands cmd, RecordingOutput @out, FakeTransport transport) Make(StatusSnapshot? status)
    {
        var output = new RecordingOutput();
        var transport = new FakeTransport();
        return (new Commands(new FakeStatus(status), transport, output), output, transport);
    }

    [Fact]
    public void Status_is_silent_and_exits_NoTray_when_nothing_is_published()
    {
        (Commands cmd, RecordingOutput output, _) = Make(null);

        int code = cmd.Status(Args.Parse(new[] { "status" }));

        Assert.Equal(ExitCode.NoTray, code);
        Assert.Empty(output.Lines);  // a prompt embedding this shows nothing when the tray is down
        Assert.Empty(output.Errors);
    }

    [Fact]
    public void Status_prints_branch_slash_desktop_for_the_current_position()
    {
        (Commands cmd, RecordingOutput output, _) = Make(TwoRows());

        int code = cmd.Status(Args.Parse(new[] { "status" }));

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("feat/b", Assert.Single(output.Lines));
    }

    [Fact]
    public void Status_branch_and_desktop_flags_print_just_that_field()
    {
        (Commands branchCmd, RecordingOutput branchOut, _) = Make(TwoRows());
        Assert.Equal(ExitCode.Ok, branchCmd.Status(Args.Parse(new[] { "status", "--branch" })));
        Assert.Equal("feat", Assert.Single(branchOut.Lines));

        (Commands deskCmd, RecordingOutput deskOut, _) = Make(TwoRows());
        Assert.Equal(ExitCode.Ok, deskCmd.Status(Args.Parse(new[] { "status", "--desktop" })));
        Assert.Equal("b", Assert.Single(deskOut.Lines));
    }

    [Fact]
    public void List_marks_the_current_row_and_prints_one_line_per_row()
    {
        (Commands cmd, RecordingOutput output, _) = Make(TwoRows());

        int code = cmd.List(Args.Parse(new[] { "list" }));

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal(2, output.Lines.Count);
        Assert.Contains("main", output.Lines[0]);
        Assert.StartsWith(" ", output.Lines[0]);              // not the current row → blank marker
        Assert.Contains("feat", output.Lines[1]);
        Assert.StartsWith("*", output.Lines[1].TrimStart());  // current row → * marker
    }

    [Fact]
    public void List_all_expands_every_desktop()
    {
        (Commands cmd, RecordingOutput output, _) = Make(TwoRows());

        cmd.List(Args.Parse(new[] { "list", "--all" }));

        // 2 row headers + 1 main desktop + 2 feat desktops.
        Assert.Equal(5, output.Lines.Count);
        Assert.Contains(output.Lines, l => l.Contains("1. one"));
        Assert.Contains(output.Lines, l => l.Contains("2. b"));
    }

    [Fact]
    public void Goto_without_a_target_is_a_usage_error_and_never_touches_the_tray()
    {
        (Commands cmd, RecordingOutput output, FakeTransport transport) = Make(TwoRows());

        int code = cmd.Goto(Args.Parse(new[] { "goto" }));

        Assert.Equal(ExitCode.BadUsage, code);
        Assert.NotEmpty(output.Errors);
        Assert.Null(transport.Sent); // no target resolved → nothing sent
    }

    [Fact]
    public void Goto_sends_a_goto_request_and_reports_the_tray_result()
    {
        (Commands ok, _, FakeTransport okTransport) = Make(TwoRows());
        int okCode = ok.Goto(Args.Parse(new[] { "goto", "main" }));
        Assert.Equal(ExitCode.Ok, okCode);
        Assert.NotNull(okTransport.Sent);
        Assert.Equal(ControlRequest.CommandGoto, okTransport.Sent!.Command);

        // A tray refusal is surfaced as its own exit code, not swallowed.
        (Commands fail, RecordingOutput failOut, FakeTransport failTransport) = Make(TwoRows());
        failTransport.Response = ControlResponse.Failure(ExitCode.Failed, "nope");
        int failCode = fail.Goto(Args.Parse(new[] { "goto", "main" }));
        Assert.Equal(ExitCode.Failed, failCode);
        Assert.Contains("nope", Assert.Single(failOut.Errors));
    }
}
