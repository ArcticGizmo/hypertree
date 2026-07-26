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

    /// <summary>Switch the whole monitor array to <paramref name="id"/>. No-op if already there.</summary>
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

    /// <summary>
    /// Pin a window to all virtual desktops so it stays visible when the desktop switches. Used to
    /// keep the map overlay on screen while you navigate underneath it.
    /// </summary>
    void PinWindow(nint hwnd);

    /// <summary>Undo <see cref="PinWindow"/>.</summary>
    void UnpinWindow(nint hwnd);
}
