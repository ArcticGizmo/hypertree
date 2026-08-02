using Hypertree.Desktops;
using Hypertree.Launch;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The OS-free half of session capture: <see cref="SessionCapture.FromWindows"/> turning the windows found
/// on a desktop into the deduped set of apps to relaunch. The Win32 enumeration only fills in each
/// window's executable path and hands them here, so this is where a session's app list is actually decided.
/// </summary>
public class SessionCaptureTests
{
    private static WindowInfo Win(string title, string process, string path)
        => new(Hwnd: 1, Title: title, ProcessName: process, ExecutablePath: path);

    [Fact]
    public void Captures_the_monitor_and_title_hint_of_the_first_window_per_app()
    {
        var apps = SessionCapture.FromWindows(new[]
        {
            new WindowInfo(1, "myrepo — Code", "Code", @"C:\Code.exe", Monitor: 2),
            new WindowInfo(2, "other — Code", "Code", @"C:\Code.exe", Monitor: 1), // dedup: first window's values win
        });

        CapturedApp app = Assert.Single(apps);
        Assert.Equal(2, app.Monitor);
        Assert.Equal("myrepo — Code", app.Hint);
    }

    [Fact]
    public void Keeps_one_app_per_executable_first_window_wins()
    {
        var apps = SessionCapture.FromWindows(new[]
        {
            Win("main.cs — Code", "Code", @"C:\Prog\Code.exe"),
            Win("readme.md — Code", "Code", @"C:\Prog\Code.exe"),   // second window of the same exe
            Win("pwsh", "WindowsTerminal", @"C:\Prog\wt.exe"),
        });

        Assert.Equal(new[] { @"C:\Prog\Code.exe", @"C:\Prog\wt.exe" }, apps.Select(a => a.Path));
    }

    [Fact]
    public void Dedupes_path_case_insensitively()
    {
        var apps = SessionCapture.FromWindows(new[]
        {
            Win("a", "Code", @"C:\Prog\Code.exe"),
            Win("b", "Code", @"c:\prog\code.exe"),
        });

        Assert.Single(apps);
    }

    [Fact]
    public void Drops_windows_with_no_resolved_path()
    {
        // A window whose process we couldn't open (protected / gone) has no path and isn't relaunchable.
        var apps = SessionCapture.FromWindows(new[]
        {
            Win("mystery", "", ""),
            Win("   ", "Code", "   "),          // whitespace-only path is treated as absent
            Win("real", "Code", @"C:\Code.exe"),
        });

        Assert.Equal(@"C:\Code.exe", Assert.Single(apps).Path);
    }

    [Fact]
    public void Preserves_encounter_order()
    {
        var apps = SessionCapture.FromWindows(new[]
        {
            Win("z", "Zed", @"C:\z.exe"),
            Win("a", "Acrobat", @"C:\a.exe"),
        });

        Assert.Equal(new[] { @"C:\z.exe", @"C:\a.exe" }, apps.Select(a => a.Path));
    }

    [Fact]
    public void Name_prefers_process_then_falls_back_to_file_name()
    {
        var apps = SessionCapture.FromWindows(new[]
        {
            Win("titled", "Code", @"C:\Prog\Code.exe"),          // process name present
            Win("titled", "", @"C:\Prog\WindowsTerminal.exe"),   // no process → file name (no extension)
        });

        Assert.Equal("Code", apps[0].Name);
        Assert.Equal("WindowsTerminal", apps[1].Name);
    }
}
