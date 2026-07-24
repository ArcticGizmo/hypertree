using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// Pure selection state for phase 1 of the "move windows" flow (F2 spirit: no Win32/UI, so the whole
/// feel is unit-testable). Holds the current desktop's windows, a focus cursor, and the set of ticked
/// windows. Phase 2 — navigating the map to a destination — reuses <see cref="NavigationModel"/>; this
/// class owns only the picker.
///
/// The focus cursor is a flat index over <see cref="Windows"/>; the view maps arrow keys to a delta
/// (±1 within a row, ±columns across rows) and calls <see cref="MoveFocus"/>. Movement clamps at the
/// ends (no wrap) to match the map's navigation feel.
/// </summary>
public sealed class WindowMoveSession
{
    private readonly HashSet<nint> _selected = new();

    public IReadOnlyList<WindowInfo> Windows { get; }
    public int Focus { get; private set; }

    public WindowMoveSession(IReadOnlyList<WindowInfo> windows)
    {
        Windows = windows;
        Focus = 0;
    }

    public bool IsEmpty => Windows.Count == 0;

    /// <summary>The focused window, or null when there are none.</summary>
    public WindowInfo? Focused => Windows.Count == 0 ? null : Windows[Focus];

    public int SelectedCount => _selected.Count;

    public bool IsSelected(int index)
        => index >= 0 && index < Windows.Count && _selected.Contains(Windows[index].Hwnd);

    /// <summary>The ticked windows' handles, in <see cref="Windows"/> order (stable for the caller).</summary>
    public IReadOnlyList<nint> SelectedHwnds
        => Windows.Where(w => _selected.Contains(w.Hwnd)).Select(w => w.Hwnd).ToList();

    /// <summary>Move the focus cursor by <paramref name="delta"/>, clamped to the list (no wrap).
    /// Returns whether the focus actually moved.</summary>
    public bool MoveFocus(int delta)
    {
        if (Windows.Count == 0) return false;
        int next = Math.Clamp(Focus + delta, 0, Windows.Count - 1);
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
    /// one, so a single-window move is just focus + Enter. Returns whether anything is selected after.</summary>
    public bool EnsureFocusedSelected()
    {
        if (_selected.Count == 0 && Focused is { } w) _selected.Add(w.Hwnd);
        return _selected.Count > 0;
    }
}
