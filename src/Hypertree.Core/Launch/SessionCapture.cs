using Hypertree.Desktops;

namespace Hypertree.Launch;

/// <summary>
/// One application recorded in a captured session — the <paramref name="Path"/> handed to the shell to
/// relaunch it (a process's full executable path) and a <paramref name="Name"/> for display in any
/// confirm / card. The launch counterpart of <see cref="AppEntry"/>, but sourced from a window that was
/// open rather than a Start-menu shortcut.
/// </summary>
public sealed record CapturedApp(string Path, string Name);

/// <summary>
/// The OS-free half of session capture: turn the raw windows found on a desktop into the deduped set of
/// apps a session should relaunch. Split out from the Win32 enumeration (which fills in each window's
/// <see cref="WindowInfo.ExecutablePath"/>) so the dedupe rules are unit-testable without a real desktop —
/// mirrors how <see cref="AppCatalogFilter"/> sits in front of the Start-menu scan.
/// </summary>
public static class SessionCapture
{
    /// <summary>
    /// Collapse <paramref name="windows"/> into launchable apps: drop any window we couldn't resolve an
    /// executable path for (nothing to relaunch), then keep the first window seen per distinct path
    /// (case-insensitive — many windows of one app relaunch once), in encounter order. A window's display
    /// name prefers its process name, falling back to the executable's file name.
    /// </summary>
    public static IReadOnlyList<CapturedApp> FromWindows(IEnumerable<WindowInfo> windows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<CapturedApp>();
        foreach (WindowInfo w in windows)
        {
            string path = (w.ExecutablePath ?? "").Trim();
            if (path.Length == 0) continue;   // no path → not relaunchable → not part of the session
            if (!seen.Add(path)) continue;    // one launch per executable, first window wins
            apps.Add(new CapturedApp(path, DisplayName(w, path)));
        }
        return apps;
    }

    private static string DisplayName(WindowInfo w, string path)
    {
        string process = (w.ProcessName ?? "").Trim();
        if (process.Length > 0) return process;
        string file = System.IO.Path.GetFileNameWithoutExtension(path);
        return file.Length > 0 ? file : path;
    }
}
