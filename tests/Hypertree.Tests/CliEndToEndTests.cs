using System.Diagnostics;
using Hypertree.Status;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Runs the real <c>htree.exe</c> against a scratch state directory and asserts what a user (or a script)
/// actually gets: the text on stdout, the diagnostics on stderr, and the exit code.
/// </summary>
/// <remarks>
/// <para>Worth doing as a process rather than by calling the command methods directly, because the things
/// most likely to break are the things only a real invocation exercises: argument parsing, which stream
/// each line goes to, and whether the exit code survives. Those are also the parts other tools depend on.</para>
///
/// <para>Isolated via <c>HYPERTREE_STATE_DIR</c>, so the suite never reads or writes the state of a
/// Hypertree the developer has running — and never needs one running to pass.</para>
///
/// <para>Skipped when the executable isn't built (e.g. a Core-only build), rather than failing: the CLI is
/// Windows-facing and this suite also runs where it may not have been produced.</para>
/// </remarks>
[Collection(StatusFileCollection.Name)] // shares the process-global StatusFile.OverrideDirectory — run serially
public sealed class CliEndToEndTests : IDisposable
{
    private readonly string _dir;
    private static readonly string? Exe = FindExe();

    public CliEndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypertree-cli-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string? FindExe()
    {
        // Walk up from the test assembly to the repo root, then to the CLI's output for this configuration.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = dir.Parent?.Name ?? "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "hypertree.slnx"))) dir = dir.Parent;
        if (dir is null) return null;

        string exe = Path.Combine(dir.FullName, "src", "Hypertree.Cli", "bin", configuration, "net10.0", "htree.exe");
        if (File.Exists(exe)) return exe;

        // Configuration didn't match (a differently-named build config); take whichever one exists.
        string root = Path.Combine(dir.FullName, "src", "Hypertree.Cli", "bin");
        return System.IO.Directory.Exists(root)
            ? System.IO.Directory.GetFiles(root, "htree.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        Assert.NotNull(Exe);
        var psi = new ProcessStartInfo(Exe!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[StatusFile.DirectoryVariable] = _dir;
        // Redirected output means Output suppresses colour anyway; this makes that explicit rather than
        // leaving the assertions dependent on it.
        psi.Environment["NO_COLOR"] = "1";

        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, stdout, stderr);
    }

    private void WriteStatus()
    {
        var snapshot = new StatusSnapshot
        {
            Version = "0.1.5",
            Pid = Environment.ProcessId, // the test process stands in for a live tray
            Rows =
            {
                new StatusRow
                {
                    Kind = RowKind.Branch, Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                    Name = "perch", Cursor = 1,
                    Desktops =
                    {
                        new StatusDesktop { Id = Guid.NewGuid(), Label = "code" },
                        new StatusDesktop { Id = Guid.NewGuid(), Label = "docs" },
                    },
                },
                new StatusRow
                {
                    Kind = RowKind.Main, Name = "main", Cursor = 0,
                    Desktops = { new StatusDesktop { Id = Guid.NewGuid(), Label = "Desktop 1" } },
                },
            },
            Current = new StatusPosition { Row = 0, Desktop = 1 },
        };

        StatusFile.OverrideDirectory(_dir);
        try { StatusFile.Write(snapshot); }
        finally { StatusFile.OverrideDirectory(null); }
    }

    // htree is part of the solution, so it is always built alongside this suite; a missing binary is a
    // real failure rather than a reason to quietly pass.
    private static void RequireExe()
        => Assert.True(Exe is not null, "htree.exe was not found — build the solution, not just the tests.");

    [Fact]
    public void Status_prints_branch_slash_desktop()
    {
        RequireExe();
        WriteStatus();

        var (code, stdout, _) = Run("status");

        Assert.Equal(0, code);
        Assert.Equal("perch/docs", stdout.Trim());
    }

    [Fact]
    public void Status_is_silent_and_non_zero_with_no_tray()
    {
        RequireExe();
        // Deliberate: a shell prompt embedding this must render nothing at all when Hypertree isn't up,
        // rather than an error or a placeholder.
        var (code, stdout, stderr) = Run("status");

        Assert.Equal(1, code);
        Assert.Equal("", stdout.Trim());
        Assert.Equal("", stderr.Trim());
    }

    [Fact]
    public void Status_can_print_just_the_branch_or_just_the_desktop()
    {
        RequireExe();
        WriteStatus();

        Assert.Equal("perch", Run("status", "--branch").stdout.Trim());
        Assert.Equal("docs", Run("status", "--desktop").stdout.Trim());
    }

    [Fact]
    public void List_marks_the_current_row_and_shows_each_rows_resume_desktop()
    {
        RequireExe();
        WriteStatus();

        var (code, stdout, _) = Run("list");

        Assert.Equal(0, code);
        string[] lines = stdout.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("*", lines[0]);          // cursor is on perch
        Assert.Contains("perch", lines[0]);
        Assert.Contains("docs", lines[0]);          // its resume point, not "code"
        Assert.Contains("2 desktops", lines[0]);
        Assert.StartsWith(" ", lines[1]);
        Assert.Contains("main", lines[1]);
        Assert.Contains("1 desktop", lines[1]);     // singular
    }

    [Fact]
    public void List_all_expands_desktops_with_one_based_positions()
    {
        RequireExe();
        WriteStatus();

        var (code, stdout, _) = Run("list", "--all");

        Assert.Equal(0, code);
        Assert.Contains("1. code", stdout);
        Assert.Contains("2. docs", stdout);
        Assert.Contains("1. Desktop 1", stdout);
    }

    [Fact]
    public void Json_output_is_the_published_snapshot_verbatim()
    {
        RequireExe();
        WriteStatus();

        var (code, stdout, _) = Run("list", "--json");

        Assert.Equal(0, code);
        // Round-trips through the same contract the tray writes, so a consumer can rely on the shape.
        StatusSnapshot? parsed = System.Text.Json.JsonSerializer.Deserialize(
            stdout, Hypertree.Cli.StatusJson.Indented);
        Assert.NotNull(parsed);
        Assert.Equal(new[] { "perch", "main" }, parsed!.Rows.Select(r => r.Name));
        Assert.Equal("docs", parsed.CurrentDesktop!.Label);
    }

    [Fact]
    public void An_unknown_target_exits_two_and_says_so_on_stderr()
    {
        RequireExe();
        WriteStatus();

        var (code, stdout, stderr) = Run("goto", "nope");

        Assert.Equal(2, code);
        Assert.Equal("", stdout.Trim()); // nothing on stdout, so a pipe stays clean
        Assert.Contains("nope", stderr);
    }

    [Fact]
    public void Goto_with_no_tray_exits_one_before_trying_to_resolve()
    {
        RequireExe();

        var (code, _, stderr) = Run("goto", "perch");

        Assert.Equal(1, code);
        Assert.Contains("No Hypertree tray", stderr);
    }

    [Fact]
    public void Goto_with_no_target_is_a_usage_error()
    {
        RequireExe();
        WriteStatus();

        var (code, _, stderr) = Run("goto");

        Assert.Equal(3, code);
        Assert.Contains("goto needs a target", stderr);
    }

    [Fact]
    public void An_unknown_command_is_a_usage_error()
    {
        RequireExe();

        var (code, _, stderr) = Run("frobnicate");

        Assert.Equal(3, code);
        Assert.Contains("Unknown command", stderr);
    }

    [Fact]
    public void Help_and_version_succeed_and_a_bare_invocation_does_not()
    {
        RequireExe();

        Assert.Equal(0, Run("help").code);
        Assert.Equal(0, Run("--help").code);
        Assert.Equal(0, Run("--version").code);
        Assert.Equal(3, Run().code); // no command usually means a script built an empty argument
    }

    [Fact]
    public void A_misspelled_flag_is_a_usage_error_not_a_silent_ignore()
    {
        RequireExe();

        // --jsonn is the canonical trap: without the guard it's ignored, status prints human output, and a
        // script that asked for JSON silently mis-parses. It must fail loudly (BadUsage) instead.
        var (code, _, stderr) = Run("status", "--jsonn");

        Assert.Equal(3, code);
        Assert.Contains("--jsonn", stderr);
    }
}
