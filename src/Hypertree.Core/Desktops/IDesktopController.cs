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

    /// <summary>Switch the whole monitor array to <paramref name="id"/>. No-op if already there.</summary>
    void SwitchTo(DesktopId id);

    /// <summary>Create a new desktop, name it, and return its id.</summary>
    DesktopId Create(string name);

    /// <summary>Rename an existing desktop.</summary>
    void Rename(DesktopId id, string name);

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
}
