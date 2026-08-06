namespace Hypertree.WindowLayout;

/// <summary>
/// A pixel rectangle by position + size. Integer, OS-free — the window-geometry counterpart to the
/// UI-side <see cref="Hypertree.Layout.LayoutRect"/> (which is double-based, for drawing the map). Kept in
/// this namespace so the two never get confused: this one is "where a window sits on a monitor", that one
/// is "where a tile sits on the board".
/// </summary>
public readonly record struct Recti(int Left, int Top, int Width, int Height);

/// <summary>How a window is shown — the three states <c>GetWindowPlacement</c> distinguishes.</summary>
public enum ShowState { Normal, Maximized, Minimized }

/// <summary>
/// One physical monitor, identified <em>stably</em>. <see cref="StableId"/> is the EDID-derived device path
/// (from <c>QueryDisplayConfig</c>), which survives a dock cycle — unlike the GDI name (<c>\\.\DISPLAY1</c>),
/// which reshuffles as displays come and go and must never be used as a key. <see cref="Friendly"/> is for
/// the UI only ("DELL P2725DE"); two identical panels share a friendly name but keep distinct stable ids.
/// </summary>
public sealed record MonitorRef(string StableId, string Friendly, Recti Bounds, bool IsPrimary, uint Dpi);

/// <summary>
/// Where one top-level window sat, captured for restore. <see cref="Hwnd"/> is the same-session match key
/// (Phase 1): unplugging a monitor doesn't restart processes, so handles survive a dock cycle.
/// <see cref="NormalOffset"/> is the window's normal-position rectangle expressed as an offset from its
/// monitor's origin, so restore can re-anchor it wherever that monitor lands next dock; the offset cancels
/// on a same-monitor round-trip, keeping it exact.
/// </summary>
public sealed record WindowPlacement(
    long Hwnd, string MonitorStableId, string Title, Recti NormalOffset, ShowState Show);

/// <summary>
/// A captured arrangement: which windows were where, across the monitor set present at capture. Keyed by
/// <see cref="SetKey"/> (see <see cref="MonitorSet.Key"/>) so a redock restores the layout belonging to the
/// dock you reconnected, not merely the last one saved. <see cref="Monitors"/> is retained for the picker
/// caption and preview.
/// </summary>
public sealed record MonitorLayoutSnapshot(
    string SetKey, IReadOnlyList<MonitorRef> Monitors, IReadOnlyList<WindowPlacement> Windows);

/// <summary>A snapshot saved under a user-chosen name (the manual "save monitor layout" path).</summary>
public sealed record NamedMonitorLayout(string Name, MonitorLayoutSnapshot Layout);

/// <summary>
/// The tally a restore returns — best-effort by contract, so the count is how the app reports what it could
/// and couldn't do rather than an all-or-nothing throw. <see cref="Gone"/>: the window closed since capture
/// (same-session HWND no longer valid). <see cref="MonitorMissing"/>: that window's monitor isn't present
/// now. <see cref="Refused"/>: the OS rejected the move (elevated / UWP window we can't touch).
/// </summary>
public readonly record struct RestoreReport(int Placed, int Gone, int MonitorMissing, int Refused)
{
    public int Total => Placed + Gone + MonitorMissing + Refused;
}

/// <summary>
/// Diagnostic: what happened to one window during a <see cref="IWindowLayoutController.RestoreTraced"/>
/// — enough to see why a particular window (e.g. Slack) won't move. Unlike <see cref="WindowPlacement"/>,
/// the rectangles here are <em>actual screen rectangles</em> (<c>GetWindowRect</c>), read before and right
/// after the move, so a call that "succeeds" but doesn't shift the window is visible.
/// </summary>
public sealed record WindowRestoreTrace(
    long Hwnd, string Title, string ProcessName, string ClassName,
    string WantedMonitor, bool MonitorPresent, ShowState WantedShow,
    Recti BeforeRect, Recti TargetRect, Recti AfterRect,
    bool SetResult, int LastError, string Outcome);

/// <summary>Diagnostic: one window's live geometry, re-read by handle — used a beat after a restore to
/// catch a window that moved and then snapped itself back (the classic Electron behaviour).</summary>
public sealed record WindowProbe(
    bool Exists, Recti Rect, string MonitorStableId, string MonitorFriendly, ShowState Show);

/// <summary>
/// What a restore would actually do, decided against the live state: how many windows are on the wrong
/// monitor (<see cref="ToMove"/> — nothing to prompt for when this is zero), and whether any of those is a
/// maximized window that must cross monitors (<see cref="NeedsCurtain"/>) — the case that "pops" visibly,
/// so the app hides it behind a loading curtain. A same-monitor or non-maximized move doesn't pop, so no
/// curtain is needed.
/// </summary>
public readonly record struct RestorePlan(int ToMove, bool NeedsCurtain);

/// <summary>Identity for "these exact screens", independent of their order or GDI names.</summary>
public static class MonitorSet
{
    /// <summary>
    /// A deterministic, order-independent key for a monitor set: its stable ids sorted and hashed (FNV-1a).
    /// Two monitors present in either enumeration order yield the same key; adding or removing one changes
    /// it. Prefixed with the count for at-a-glance readability in logs and the UI ("3m-e7f60f7f").
    /// </summary>
    public static string Key(IEnumerable<MonitorRef> monitors)
    {
        var ids = monitors.Select(m => m.StableId).ToList();
        ids.Sort(StringComparer.Ordinal);
        uint h = 2166136261;
        foreach (string id in ids)
            foreach (char c in id) { h ^= c; h *= 16777619; }
        return $"{ids.Count}m-{h:x8}";
    }
}
