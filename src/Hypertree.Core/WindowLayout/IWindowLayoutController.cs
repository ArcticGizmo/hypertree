namespace Hypertree.WindowLayout;

/// <summary>
/// The single seam over the OS's window geometry and physical-monitor topology — the
/// <see cref="Desktops.IDesktopController"/> of the monitor-layout axis. Every call that reads or moves
/// windows in screen space, or enumerates monitors with stable ids, lives behind this interface,
/// implemented in Hypertree.Platform.Windows. Isolating it here keeps <see cref="MonitorLayoutService"/>
/// and its decision logic OS-free and unit-testable against a fake, and confines the fragile Win32
/// (<c>QueryDisplayConfig</c>, <c>GetWindowPlacement</c>/<c>SetWindowPlacement</c>) to one file.
/// </summary>
public interface IWindowLayoutController
{
    /// <summary>
    /// The monitors present now, each with a <em>stable</em> id (see <see cref="MonitorRef"/>). Order is not
    /// significant — callers key on <see cref="MonitorSet.Key"/>, never position.
    /// </summary>
    IReadOnlyList<MonitorRef> Monitors();

    /// <summary>
    /// Capture every countable top-level window's placement and the monitor it sits on — the same "real app
    /// window" set the map counts. Best-effort: a window whose placement can't be read is skipped, never
    /// thrown.
    /// </summary>
    MonitorLayoutSnapshot Snapshot();

    /// <summary>
    /// Put windows back where <paramref name="snapshot"/> recorded them, re-anchored onto whichever present
    /// monitors carry the matching stable ids. Same-session HWND matching (Phase 1): a window that has since
    /// closed is skipped. Best-effort per window — see <see cref="RestoreReport"/> for the tally.
    /// </summary>
    RestoreReport Restore(MonitorLayoutSnapshot snapshot);

    /// <summary>
    /// Diagnostic restore: performs the same moves as <see cref="Restore"/> but returns a per-window trace
    /// (before/after screen rectangles, the <c>SetWindowPlacement</c> result and last Win32 error, class and
    /// process) so a window that won't cooperate can be diagnosed. For debugging only.
    /// </summary>
    IReadOnlyList<WindowRestoreTrace> RestoreTraced(MonitorLayoutSnapshot snapshot);

    /// <summary>Diagnostic: re-read one window's live geometry by handle, to see where it ended up a moment
    /// after a restore (catching a window that moved then reverted). For debugging only.</summary>
    WindowProbe Probe(long hwnd);
}
