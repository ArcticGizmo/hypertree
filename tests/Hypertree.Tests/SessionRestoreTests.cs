using Hypertree.Launch;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The OS-free half of session restore: <see cref="SessionRestore.ToLaunch"/> deciding which saved apps
/// still need launching, given what already has a window on the target desktop — so restore tops a desktop
/// up rather than duplicating. The App's switch/launch orchestration sits on top of this.
/// </summary>
public class SessionRestoreTests
{
    private static CapturedApp App(string path) => new(path, System.IO.Path.GetFileNameWithoutExtension(path));

    [Fact]
    public void Skips_apps_already_present_case_insensitively()
    {
        var toLaunch = SessionRestore.ToLaunch(
            new[] { App(@"C:\Prog\Code.exe"), App(@"C:\Prog\wt.exe") },
            present: new[] { @"c:\prog\CODE.exe" });

        Assert.Equal(new[] { @"C:\Prog\wt.exe" }, toLaunch.Select(a => a.Path));
    }

    [Fact]
    public void Launches_everything_when_nothing_present()
    {
        var saved = new[] { App(@"C:\a.exe"), App(@"C:\b.exe") };
        var toLaunch = SessionRestore.ToLaunch(saved, present: System.Array.Empty<string>());
        Assert.Equal(new[] { @"C:\a.exe", @"C:\b.exe" }, toLaunch.Select(a => a.Path));
    }

    [Fact]
    public void Launches_nothing_when_all_present()
    {
        var saved = new[] { App(@"C:\a.exe"), App(@"C:\b.exe") };
        var toLaunch = SessionRestore.ToLaunch(saved, present: new[] { @"C:\a.exe", @"C:\b.exe" });
        Assert.Empty(toLaunch);
    }

    [Fact]
    public void Blank_present_paths_are_ignored()
    {
        // A window whose executable path couldn't be resolved is "" — it can't mask a saved app.
        var toLaunch = SessionRestore.ToLaunch(
            new[] { App(@"C:\a.exe") },
            present: new[] { "", "   " });

        Assert.Equal(@"C:\a.exe", Assert.Single(toLaunch).Path);
    }

    [Fact]
    public void Preserves_saved_order()
    {
        var saved = new[] { App(@"C:\z.exe"), App(@"C:\a.exe"), App(@"C:\m.exe") };
        var toLaunch = SessionRestore.ToLaunch(saved, present: new[] { @"C:\a.exe" });
        Assert.Equal(new[] { @"C:\z.exe", @"C:\m.exe" }, toLaunch.Select(a => a.Path));
    }
}
