using Hypertree.Desktops;
using Hypertree.Loadouts;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The OS-free decisions in a loadout restore (<see cref="LoadoutRun"/>): flattening a loadout into an ordered
/// run, matching the window a launch produced (new handle + matching executable path), and choosing which
/// windows are safe to close on an abort. The App owns the timing and Win32; this is the testable kernel.
/// </summary>
public class LoadoutRunTests
{
    private static readonly IReadOnlySet<int> NoPids = new HashSet<int>();

    private static LoadoutStep Step(string target) => new() { Target = target, Name = "x", Placement = new Placement { Desktop = "api" } };
    private static WindowInfo Win(nint hwnd, string path, int pid = 0) => new(hwnd, "t", "p", path, ProcessId: pid);
    private static IReadOnlySet<int> Pids(params int[] pids) => new HashSet<int>(pids);
    private static IReadOnlySet<nint> Hwnds(params nint[] hwnds) => new HashSet<nint>(hwnds);

    private static Loadout TwoDesktopLoadout() => new()
    {
        Name = "feat",
        Desktops =
        {
            new LoadoutDesktop { Label = "api", Steps = { Step(@"C:\Code.exe"), Step(@"C:\wt.exe") } },
            new LoadoutDesktop { Label = "web", Steps = { Step(@"C:\firefox.exe") } },
        },
    };

    [Fact]
    public void Plan_flattens_desktop_then_step_order_carrying_the_label()
    {
        var steps = LoadoutRun.Plan(TwoDesktopLoadout());
        Assert.Equal(new[] { @"C:\Code.exe", @"C:\wt.exe", @"C:\firefox.exe" }, steps.Select(s => s.Step.Target));
        Assert.Equal(new[] { "api", "api", "web" }, steps.Select(s => s.DesktopLabel));
        Assert.All(steps, s => Assert.Equal(StepState.NotStarted, s.State));
    }

    [Fact]
    public void MatchNewWindow_finds_a_new_handle_with_a_matching_path()
    {
        var before = new HashSet<nint> { 1 };
        var after = new[] { Win(1, @"C:\old.exe"), Win(2, @"C:\Code.exe") };
        Assert.Equal(2, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_ignores_pre_existing_windows_even_if_path_matches()
    {
        // The app was already open (handle 5 present before): no NEW window → 0 → caller marks AlreadyOpen.
        var before = new HashSet<nint> { 5 };
        var after = new[] { Win(5, @"C:\Code.exe") };
        Assert.Equal(0, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_matches_path_case_insensitively_and_ignores_blank_paths()
    {
        var before = new HashSet<nint>();
        var after = new[] { Win(3, "   "), Win(4, @"c:\CODE.exe") };
        Assert.Equal(4, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_returns_zero_when_no_path_matches()
    {
        var before = new HashSet<nint>();
        var after = new[] { Win(2, @"C:\other.exe") };
        Assert.Equal(0, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_matches_by_owning_pid_when_the_path_cannot()
    {
        // A shortcut / bare-name target whose path never equals the running exe: pid is the only signal.
        var before = new HashSet<nint>();
        var after = new[] { Win(7, @"C:\Program Files\Mozilla Firefox\firefox.exe", pid: 4242) };
        Assert.Equal(7, LoadoutRun.MatchNewWindow(Step("firefox"), before, after, Pids(4242)));
    }

    [Fact]
    public void MatchNewWindow_matches_a_descendant_pid()
    {
        // The launched stub (100) re-exec'd the real app (200); 200 is in the launch's subtree.
        var before = new HashSet<nint>();
        var after = new[] { Win(9, @"C:\App\real.exe", pid: 200) };
        Assert.Equal(9, LoadoutRun.MatchNewWindow(Step(@"C:\App\stub.exe"), before, after, Pids(100, 200)));
    }

    [Fact]
    public void MatchNewWindow_prefers_the_pid_match_over_a_name_match()
    {
        // Two new windows: one only name-matches, one is genuinely ours by pid. The pid one wins.
        var before = new HashSet<nint>();
        var after = new[] { Win(3, @"C:\Code.exe"), Win(4, @"C:\App\real.exe", pid: 555) };
        Assert.Equal(4, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after, Pids(555)));
    }

    [Fact]
    public void MatchNewWindow_matches_by_file_name_when_the_target_is_a_full_path()
    {
        // The target names Code.exe under one directory; the window's exe is Code.exe under another (a common
        // launcher-vs-install mismatch). File-name equality still attributes it.
        var before = new HashSet<nint>();
        var after = new[] { Win(6, @"C:\Users\me\AppData\Local\Programs\Code\Code.exe") };
        Assert.Equal(6, LoadoutRun.MatchNewWindow(Step(@"C:\Program Files\Code.exe"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_uses_a_resolved_target_name_for_the_fallback()
    {
        var before = new HashSet<nint>();
        var after = new[] { Win(8, @"C:\Program Files\Mozilla Firefox\firefox.exe") };
        Assert.Equal(8, LoadoutRun.MatchNewWindow(Step(@"C:\...\Firefox.lnk"), before, after, NoPids, targetName: "firefox.exe"));
    }

    [Fact]
    public void MatchNewWindow_matches_a_bare_command_by_stem()
    {
        // "code" (a bare command / a code.cmd shim) matches the running "Code.exe" by file-name stem.
        var before = new HashSet<nint>();
        var after = new[] { Win(5, @"C:\programs\Microsoft VS Code\Code.exe") };
        Assert.Equal(5, LoadoutRun.MatchNewWindow(Step("code"), before, after, NoPids));
    }

    [Fact]
    public void MatchNewWindow_falls_back_to_a_new_window_on_staging_when_nothing_else_matches()
    {
        // "wt" → a WindowsTerminal.exe window: no pid (packaged singleton), no name match ("wt" ≠
        // "WindowsTerminal"). It's a new window on our staging desktop, so that's the one.
        var before = new HashSet<nint>();
        var after = new[] { Win(6, @"C:\Program Files\WindowsApps\…\WindowsTerminal.exe") };
        Assert.Equal(6, LoadoutRun.MatchNewWindow(Step("wt"), before, after, NoPids, onStaging: Hwnds(6)));
    }

    [Fact]
    public void MatchNewWindow_does_not_use_staging_for_a_window_that_was_already_open()
    {
        // A window present before the launch is never a match, even if it sits on staging now.
        var before = new HashSet<nint> { 6 };
        var after = new[] { Win(6, @"C:\App\thing.exe") };
        Assert.Equal(0, LoadoutRun.MatchNewWindow(Step("thing"), before, after, NoPids, onStaging: Hwnds(6)));
    }

    [Fact]
    public void MatchNewWindow_prefers_a_name_match_over_a_staging_only_window()
    {
        // Two new windows: one only on staging, one that also matches by name. Name is the more specific
        // signal, so it wins over the bare staging fallback.
        var before = new HashSet<nint>();
        var after = new[] { Win(7, @"C:\other\unrelated.exe"), Win(8, @"C:\App\Code.exe") };
        Assert.Equal(8, LoadoutRun.MatchNewWindow(Step("code"), before, after, NoPids, onStaging: Hwnds(7)));
    }

    [Fact]
    public void CleanupCandidates_are_matched_windows_not_yet_placed()
    {
        var steps = new[]
        {
            new RunStep(Step(@"C:\a.exe"), "api") { State = StepState.Done, Window = 10 },       // placed — leave it
            new RunStep(Step(@"C:\b.exe"), "api") { State = StepState.Placing, Window = 11 },     // on staging — close
            new RunStep(Step(@"C:\c.exe"), "api") { State = StepState.Creating, Window = 12 },    // matched, mid-move — close
            new RunStep(Step(@"C:\d.exe"), "api") { State = StepState.AlreadyOpen, Window = 0 },   // nothing we launched
            new RunStep(Step(@"C:\e.exe"), "api") { State = StepState.Error, Window = 0 },         // never appeared
        };

        Assert.Equal(new nint[] { 11, 12 }, LoadoutRun.CleanupCandidates(steps));
    }
}
