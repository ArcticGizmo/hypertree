using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// Pure selection state for the window-picker flows — the "move windows" and "pull windows" grids (F2
/// spirit: no Win32/UI, so the whole feel is unit-testable). Holds the candidate windows, a case-insensitive
/// search filter, a focus cursor, and the set of ticked windows. (Move's phase 2 — navigating the map to a
/// destination — reuses <see cref="NavigationModel"/>; this class owns only the picker.)
///
/// The focus cursor is a flat index over the <see cref="Visible"/> (filtered) windows; the view maps arrow
/// keys to a delta (±1 within a row, ±columns across rows) and calls <see cref="MoveFocus"/>. Movement clamps
/// at the ends (no wrap) to match the map's navigation feel. Selection is keyed by window handle, so a ticked
/// window stays ticked as the filter changes — you can search, tick, refine the search, tick more, and drop
/// the whole set.
/// </summary>
public sealed class WindowMoveSession
{
    private readonly HashSet<nint> _selected = new();
    private readonly List<WindowInfo> _all;
    private List<WindowInfo> _visible;
    private string _filter = "";

    /// <summary>Every candidate window (the unfiltered list).</summary>
    public IReadOnlyList<WindowInfo> Windows => _all;

    /// <summary>The windows currently shown — <see cref="Windows"/> narrowed by <see cref="Filter"/>.</summary>
    public IReadOnlyList<WindowInfo> Visible => _visible;

    /// <summary>The active search string (empty ⇒ everything is visible).</summary>
    public string Filter => _filter;

    /// <summary>Focus cursor as an index into <see cref="Visible"/>.</summary>
    public int Focus { get; private set; }

    public WindowMoveSession(IReadOnlyList<WindowInfo> windows)
    {
        _all = windows.ToList();
        _visible = _all;
        Focus = 0;
    }

    /// <summary>No candidate windows at all (distinct from "the filter hides them all").</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>The focused window, or null when nothing is visible.</summary>
    public WindowInfo? Focused => _visible.Count == 0 ? null : _visible[Focus];

    public int SelectedCount => _selected.Count;

    /// <summary>Whether the window at <paramref name="visibleIndex"/> (an index into <see cref="Visible"/>)
    /// is ticked.</summary>
    public bool IsSelected(int visibleIndex)
        => visibleIndex >= 0 && visibleIndex < _visible.Count && _selected.Contains(_visible[visibleIndex].Hwnd);

    /// <summary>The ticked windows' handles, in <see cref="Windows"/> order (stable for the caller, and
    /// independent of the current filter).</summary>
    public IReadOnlyList<nint> SelectedHwnds
        => _all.Where(w => _selected.Contains(w.Hwnd)).Select(w => w.Hwnd).ToList();

    /// <summary>Move the focus cursor by <paramref name="delta"/>, clamped to the visible set (no wrap).
    /// Returns whether the focus actually moved.</summary>
    public bool MoveFocus(int delta)
    {
        if (_visible.Count == 0) return false;
        int next = Math.Clamp(Focus + delta, 0, _visible.Count - 1);
        if (next == Focus) return false;
        Focus = next;
        return true;
    }

    /// <summary>Tick / untick the focused window.</summary>
    public void ToggleSelected()
    {
        if (Focused is not { } w) return;
        if (!_selected.Remove(w.Hwnd)) _selected.Add(w.Hwnd);
    }

    /// <summary>Convenience for Enter-with-nothing-ticked: if no window is selected, tick the focused
    /// one, so a single-window pick is just focus + Enter. Returns whether anything is selected after.</summary>
    public bool EnsureFocusedSelected()
    {
        if (_selected.Count == 0 && Focused is { } w) _selected.Add(w.Hwnd);
        return _selected.Count > 0;
    }

    /// <summary>
    /// Narrow the visible set to windows whose title, process, or source-desktop name contains
    /// <paramref name="filter"/> (case-insensitive). Focus stays on the same window when it survives the
    /// filter, otherwise it snaps to the top. Ticks are untouched (kept by handle). Returns whether the
    /// visible set changed.
    /// </summary>
    public bool SetFilter(string? filter)
    {
        string next = (filter ?? "").Trim();
        if (next == _filter) return false;

        nint? focusedHwnd = Focused?.Hwnd;
        _filter = next;
        _visible = next.Length == 0 ? _all : _all.Where(w => Matches(w, next)).ToList();

        int keep = focusedHwnd is { } h ? _visible.FindIndex(w => w.Hwnd == h) : -1;
        Focus = keep >= 0 ? keep : 0;
        if (Focus >= _visible.Count) Focus = Math.Max(0, _visible.Count - 1);
        return true;
    }

    private static bool Matches(WindowInfo w, string q)
        => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || w.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || w.DesktopName.Contains(q, StringComparison.OrdinalIgnoreCase);
}
