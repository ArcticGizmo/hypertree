namespace Hypertree.Desktops;

/// <summary>
/// The single seam over Windows' virtual desktops. Every build-fragile, undocumented COM call
/// (create / switch / move-window / enumerate / name) lives behind this interface, implemented
/// in Hypertree.Platform.Windows. Isolating it here means (a) the navigation model is testable
/// against a fake, and (b) an OS-update GUID/vtable break — or a swap to komorebi — is a
/// single-file change, never a change to Hypertree's logic. (See docs/design/m0-findings.md for
/// the proven native implementation and its per-build GUIDs.)
/// </summary>
public interface IDesktopController
{
    /// <summary>Number of virtual desktops the OS currently has.</summary>
    int Count { get; }

    /// <summary>Number of physical monitors attached — the number of per-desktop slots the loadout builder
    /// draws, and the range a step's <see cref="WindowInfo.Monitor"/> / placement index runs over. At least 1.</summary>
    int MonitorCount { get; }

    /// <summary>The desktop currently shown across the monitor array.</summary>
    DesktopId Current { get; }

    /// <summary>All desktops in OS order (ordinal 0..Count-1).</summary>
    IReadOnlyList<DesktopInfo> List();

    /// <summary>
    /// How many application windows currently sit on each desktop, keyed by id. Desktops with no windows
    /// may be absent (treat a missing id as zero). Best-effort and advisory only — it drives the
    /// at-a-glance counts on the map, never navigation — so an inexact count is acceptable.
    /// </summary>
    IReadOnlyDictionary<DesktopId, int> WindowCounts();

    /// <summary>
    /// The application windows currently on <paramref name="id"/> — the same "countable" windows
    /// <see cref="WindowCounts"/> tallies, but with each window's handle, title and process name so
    /// they can be listed and moved (the "move windows" picker). Best-effort; order is enumeration
    /// order (roughly Z-order).
    /// </summary>
    IReadOnlyList<WindowInfo> WindowsOn(DesktopId id);

    /// <summary>
    /// Every application window across <em>all</em> desktops — the superset <see cref="WindowsOn"/> returns
    /// per desktop — each with its handle, title, process name and executable path. Session restore
    /// snapshots this before launching a loadout step and diffs it after, matching the window a launch
    /// produced by executable path. Best-effort, enumeration order.
    /// </summary>
    IReadOnlyList<WindowInfo> AllWindows();

    /// <summary>
    /// Which desktop <paramref name="hwnd"/> is currently on, or null when it can't be attributed (a window
    /// pinned to all desktops, unassigned, or a stale handle). Restore uses this to be <em>certain</em> a
    /// window it launched is still on the staging desktop before it ever closes one during an abort.
    /// </summary>
    DesktopId? DesktopOf(nint hwnd);

    /// <summary>Switch the whole monitor array to <paramref name="id"/>. No-op if already there. Also
    /// hands the foreground to a window on the destination, the way the OS's own switcher does — a bare
    /// desktop switch leaves the previous desktop's focused window as an unreachable, cloaked foreground
    /// window (see docs/design/foreground-handover-on-switch.md), which this prevents.</summary>
    void SwitchTo(DesktopId id);

    /// <summary>Create a new desktop, name it, and return its id.</summary>
    DesktopId Create(string name);

    /// <summary>Rename an existing desktop.</summary>
    void Rename(DesktopId id, string name);

    /// <summary>
    /// Move <paramref name="id"/> to ordinal <paramref name="index"/> in the OS order — the same reorder
    /// Task View's own drag performs. The main timeline <em>is</em> the OS order (every desktop we haven't
    /// branched, in the order the OS lists them), so dropping a desktop at a position on main only sticks
    /// if the OS agrees. Best-effort: a stale id, or a shell that refuses, is a no-op rather than a throw.
    /// </summary>
    void Reorder(DesktopId id, int index);

    /// <summary>
    /// Remove <paramref name="id"/>; any windows on it fall back to <paramref name="fallback"/>.
    /// </summary>
    void Remove(DesktopId id, DesktopId fallback);

    /// <summary>Read a desktop's current name (empty string if unnamed).</summary>
    string GetName(DesktopId id);

    /// <summary>
    /// Move a top-level window (by handle) onto <paramref name="id"/>. Works for foreign windows
    /// (terminals, editors) — the whole point of provisioning a scope's window set.
    /// </summary>
    void MoveWindowToDesktop(nint hwnd, DesktopId id);

    /// <summary>Ask a window to close — a graceful <c>WM_CLOSE</c>, as if its ✕ were clicked. Best-effort: a
    /// window that ignores it, or puts up a "save changes?" prompt, is left as-is. Restore uses this only to
    /// clear windows it launched onto the <em>staging</em> desktop when a restore is aborted.</summary>
    void CloseWindow(nint hwnd);

    /// <summary>
    /// Move a window onto <paramref name="monitor"/> (1-based, matching the index <see cref="WindowsOn"/>
    /// records in <see cref="WindowInfo.Monitor"/>). Best-effort placement — keeps the window's size,
    /// re-maximising it if it was maximised — used by loadout restore to put a window back on the screen it
    /// was captured from. Exact position/size is a later phase. Out-of-range or a stale handle is a no-op.
    /// </summary>
    void MoveWindowToMonitor(nint hwnd, int monitor);

    /// <summary>
    /// Pin a window to all virtual desktops so it stays visible when the desktop switches. Used to
    /// keep the map overlay on screen while you navigate underneath it.
    /// </summary>
    void PinWindow(nint hwnd);

    /// <summary>Undo <see cref="PinWindow"/>.</summary>
    void UnpinWindow(nint hwnd);
}
