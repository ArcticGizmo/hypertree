using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Hypertree.Desktops;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The spatial map's pointer-drag gesture engine, lifted out of <see cref="SpatialOverlay"/> so the overlay
/// keeps the model edits and this owns only the press → drag → drop state machine. A plain drag moves the
/// room under the selection; a ⇧-drag its contiguous block; a drag of a room in the selected group moves the
/// whole group. The room hosts follow the pointer directly — the board is never rebuilt mid-gesture, so the
/// pointer capture (and the drag) survives — and on release the raw pixel offset snaps to whole cells and the
/// move is committed once.
/// </summary>
/// <remarks>
/// It reaches back into the overlay only through a handful of callbacks (what the cursor is on, which rooms a
/// grab carries, a room's live host, the cell stride, and how to commit or snap back), so the coupling is a
/// small explicit seam rather than shared mutable state. The overlay owns the root's pointer-event wiring and
/// forwards each event here; hover (a non-drag concern) stays with the overlay.
/// </remarks>
internal sealed class RoomDragController
{
    // Pixels the pointer must travel before a press becomes a drag — below it, a press is a click (select).
    private const double DragThreshold = 6;

    private readonly Control _root;
    private readonly Func<DesktopId?> _cursor;                                  // the room the selection is on
    private readonly Func<DesktopId, bool, IReadOnlyList<SpatialRoom>?> _dragSetOf; // rooms a grab of id (with ⇧?) carries; null ⇒ not draggable
    private readonly Func<DesktopId, Control?> _hostOf;                         // a room's live host control, to move it directly
    private readonly Func<(double X, double Y)> _stride;                        // pixel→cell stride at the current zoom
    private readonly Action<IReadOnlyDictionary<DesktopId, GridPos>> _commit;   // apply the final grid positions (persist + redraw)
    private readonly Action _snapBack;                                          // redraw so visually-moved hosts return to their cells

    private bool _tilePressed;                                          // the press bubbling up started on a room tile
    private DesktopId? _grab;                                           // the room picked up
    private bool _dragging;
    private Point _pressAt;
    private readonly Dictionary<DesktopId, Point> _hostBase = new();    // host screen top-left at grab
    private readonly Dictionary<DesktopId, GridPos> _gridBase = new();  // grid position at grab, for the drop

    public RoomDragController(
        Control root, Func<DesktopId?> cursor, Func<DesktopId, bool, IReadOnlyList<SpatialRoom>?> dragSetOf,
        Func<DesktopId, Control?> hostOf, Func<(double X, double Y)> stride,
        Action<IReadOnlyDictionary<DesktopId, GridPos>> commit, Action snapBack)
    {
        _root = root;
        _cursor = cursor;
        _dragSetOf = dragSetOf;
        _hostOf = hostOf;
        _stride = stride;
        _commit = commit;
        _snapBack = snapBack;
    }

    /// <summary>True while a room is picked up (from the press, before or during the drag) — the overlay routes
    /// pointer-moves here rather than to hover while this holds.</summary>
    public bool Grabbing => _grab is not null;

    /// <summary>The painter's per-room click ran on the way up, so the bubbling press started on a tile — the
    /// signal that a following drag is legitimate (an empty-canvas press never drags).</summary>
    public void NoteTilePressed() => _tilePressed = true;

    public void Press(PointerPressedEventArgs e)
    {
        bool onTile = _tilePressed;                             // the tile's own handler ran first, on the way up
        _tilePressed = false;
        Cancel();
        if (e.ClickCount >= 2) return;                          // a double-click is "switch", never a drag
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        if (!onTile || _cursor() is not { } id) return;
        if (_dragSetOf(id, e.KeyModifiers.HasFlag(KeyModifiers.Shift)) is not { } set) return;

        _grab = id;
        _pressAt = e.GetPosition(_root);
        _hostBase.Clear();
        _gridBase.Clear();
        foreach (SpatialRoom r in set)
        {
            _gridBase[r.Id] = r.Pos;
            if (_hostOf(r.Id) is { } host) _hostBase[r.Id] = new Point(Canvas.GetLeft(host), Canvas.GetTop(host));
        }
    }

    public void Move(PointerEventArgs e)
    {
        if (_grab is null) return;
        Point at = e.GetPosition(_root);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(_root);
        }
        double dx = at.X - _pressAt.X, dy = at.Y - _pressAt.Y;
        // Move the hosts only — the board is not rebuilt, so the capture (and the gesture) survives.
        foreach ((DesktopId id, Point basePt) in _hostBase)
            if (_hostOf(id) is { } host)
            {
                Canvas.SetLeft(host, basePt.X + dx);
                Canvas.SetTop(host, basePt.Y + dy);
            }
    }

    public void Release(PointerReleasedEventArgs e)
    {
        bool wasDragging = _dragging;
        Point at = e.GetPosition(_root);
        var gridBase = new Dictionary<DesktopId, GridPos>(_gridBase);
        double dx = at.X - _pressAt.X, dy = at.Y - _pressAt.Y;
        e.Pointer.Capture(null);
        _grab = null;
        _dragging = false;
        _hostBase.Clear();
        _gridBase.Clear();
        if (!wasDragging) return;                              // a click, not a drag — selection handled on press

        (double sx, double sy) = _stride();
        int gdx = (int)Math.Round(dx / sx), gdy = (int)Math.Round(dy / sy);
        if (gdx == 0 && gdy == 0) { _snapBack(); return; }     // didn't cross a cell — snap the hosts back
        var moves = new Dictionary<DesktopId, GridPos>(gridBase.Count);
        foreach ((DesktopId id, GridPos basePos) in gridBase) moves[id] = basePos.Offset(gdx, gdy);
        _commit(moves);
    }

    public void Cancel()
    {
        bool wasActive = _grab is not null || _dragging;
        _grab = null;
        _dragging = false;
        _hostBase.Clear();
        _gridBase.Clear();
        if (wasActive) _snapBack();                            // snap any visually-moved hosts back to their cells
    }
}
