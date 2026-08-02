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
    private static LoadoutStep Step(string target) => new() { Target = target, Name = "x", Placement = new Placement { Desktop = "api" } };
    private static WindowInfo Win(nint hwnd, string path) => new(hwnd, "t", "p", path);

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
        Assert.Equal(2, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after));
    }

    [Fact]
    public void MatchNewWindow_ignores_pre_existing_windows_even_if_path_matches()
    {
        // The app was already open (handle 5 present before): no NEW window → 0 → caller marks AlreadyOpen.
        var before = new HashSet<nint> { 5 };
        var after = new[] { Win(5, @"C:\Code.exe") };
        Assert.Equal(0, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after));
    }

    [Fact]
    public void MatchNewWindow_matches_path_case_insensitively_and_ignores_blank_paths()
    {
        var before = new HashSet<nint>();
        var after = new[] { Win(3, "   "), Win(4, @"c:\CODE.exe") };
        Assert.Equal(4, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after));
    }

    [Fact]
    public void MatchNewWindow_returns_zero_when_no_path_matches()
    {
        var before = new HashSet<nint>();
        var after = new[] { Win(2, @"C:\other.exe") };
        Assert.Equal(0, LoadoutRun.MatchNewWindow(Step(@"C:\Code.exe"), before, after));
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
