namespace Hypertree.Launch;

/// <summary>
/// One launchable application discovered on the machine — a display <paramref name="Name"/> and the
/// <paramref name="LaunchPath"/> handed to the shell to start it (a Start-menu <c>.lnk</c>/<c>.url</c>,
/// which shell-execute resolves to the real target). Icons are fetched separately, keyed by this same
/// path (see <see cref="IAppIconProvider"/>), so discovery stays cheap and the icons load lazily.
/// </summary>
public sealed record AppEntry(string Name, string LaunchPath);

/// <summary>
/// The list of installed applications the launcher offers — the same set Windows Search draws its "apps"
/// results from (the Start-menu shortcut trees). OS-specific; implemented in the Windows platform layer
/// and resolved by the composition root, so no UI code hard-codes the shell-folder scan.
/// </summary>
public interface IAppCatalog
{
    /// <summary>Enumerate the installed applications, deduped and sorted for display. Called each time the
    /// launcher opens so freshly-installed apps appear without a restart; a failure yields an empty list
    /// rather than throwing (a launcher with no apps is better than a crashed tray).</summary>
    IReadOnlyList<AppEntry> Discover();
}

/// <summary>
/// The outcome of an <see cref="IAppLauncher.Launch"/> call: whether the OS started anything, and the
/// process id the shell handed back when it started one. <see cref="ProcessId"/> is null when the launch
/// went through a broker that owns no useful pid (a packaged-app activation via Explorer) or when the shell
/// reused an already-running process — a loadout restore then attributes the new window by executable name
/// instead of by pid.
/// </summary>
public readonly record struct LaunchResult(bool Started, int? ProcessId)
{
    /// <summary>The launch didn't happen — a bad path, a declined UAC prompt.</summary>
    public static readonly LaunchResult Failed = new(false, null);

    /// <summary>The launch started, carrying the pid the shell reported (null when it reported none).</summary>
    public static LaunchResult Ok(int? processId) => new(true, processId);
}

/// <summary>
/// Starts things the way double-clicking them in Explorer would: an app shortcut, an <c>.exe</c>, a file,
/// a folder, or a URL — all via the shell (<c>ShellExecute</c>). Behind an interface so the launcher and
/// the custom-command runner never touch <c>Process.Start</c> directly and a non-Windows head can swap in
/// its own.
/// </summary>
public interface IAppLauncher
{
    /// <summary>Launch <paramref name="target"/> (with optional <paramref name="arguments"/> and
    /// <paramref name="workingDirectory"/>) through the shell. The result's <see cref="LaunchResult.Started"/>
    /// is false if the OS refused to start it — a bad path, a cancelled UAC prompt — which the caller surfaces
    /// rather than crashing on; <see cref="LaunchResult.ProcessId"/> carries the started pid when the shell
    /// reports one (used to attribute the window a loadout step produced).</summary>
    LaunchResult Launch(string target, string? arguments = null, string? workingDirectory = null);
}

/// <summary>
/// Supplies an app's icon as encoded PNG bytes, keyed by the same launch path as its <see cref="AppEntry"/>.
/// Kept OS-agnostic (raw bytes, no <c>HICON</c> / GDI type crossing the seam) so the UI layer just decodes
/// them into whatever image type it draws with. A miss returns null — the row simply shows no icon.
/// </summary>
public interface IAppIconProvider
{
    /// <summary>The icon for <paramref name="path"/> as PNG bytes, or null when none can be extracted.
    /// Safe to call off the UI thread; implementations must not throw (a failed extraction is a null).</summary>
    byte[]? GetIconPng(string path);
}

/// <summary>
/// The OS-free part of building the app list: turn a raw set of discovered shortcuts into the deduped,
/// sorted entries the launcher shows. Split out from the platform scan so it's unit-testable without a
/// real Start menu — the Windows catalog does the file enumeration, this decides what survives.
/// </summary>
public static class AppCatalogFilter
{
    /// <summary>
    /// Collapse <paramref name="candidates"/> (name + launch path, in discovery order) into display entries:
    /// drop blanks and uninstallers, keep the first shortcut seen for each name (case-insensitive, so the
    /// all-users and per-user Start menus don't double up), and sort alphabetically for a stable list.
    /// </summary>
    public static IReadOnlyList<AppEntry> FromShortcuts(IEnumerable<(string Name, string Path)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AppEntry>();
        foreach ((string name, string path) in candidates)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0 || string.IsNullOrWhiteSpace(path)) continue;
            if (IsUninstaller(trimmed)) continue;      // don't put "Uninstall X" a fat-fingered Enter away
            if (!seen.Add(trimmed)) continue;          // first shortcut of a given name wins
            entries.Add(new AppEntry(trimmed, path));
        }
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    // Shortcuts whose whole point is removal — kept out of the launcher so a quick type-and-Enter can't
    // trigger an uninstall. Matches the common "Uninstall …" / "… Uninstall" shortcut naming.
    private static bool IsUninstaller(string name) =>
        name.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(" Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
}
