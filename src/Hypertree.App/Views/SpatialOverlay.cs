using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.App.Views.Scene;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive <b>spatial map</b> — the second map model, presented on the same <see cref="OverlayStage"/>
/// as the row <see cref="MapOverlay"/>. Desktops are freely-placed rooms; a blue selection cursor moves over
/// a stationary map (arrow keys pick the nearest room in that direction, or click a room), <c>Enter</c> /
/// double-click switches, and <c>Tab</c> flips back to the row model. It shares the app's dead-zone
/// <see cref="MapCamera"/> with the flash and the row map, so switching models never teleports the view.
///
/// M2 covers viewing, navigation, jump and the model swap. Placement, groups, delete and tidy — the edits —
/// arrive in later milestones; the overlay is built to grow those in without touching the row map.
/// </summary>
internal sealed class SpatialOverlay : IStageContent
{
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly Color LegendBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22);
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A);
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly OverlayStage _stage;
    private readonly MapCamera _camera;
    private readonly Grid _root = new();

    private SpatialSource _source = new(Array.Empty<SpatialGroupSource>());
    private SpatialState _state = new();
    private DesktopId? _cursor;   // the blue selection; null until homed onto the current desktop
    private bool _initialised;

    // The last render's room hit-rects (screen space) and scene, so a drag can hit-test and read effective
    // positions without rebuilding.
    private readonly List<(DesktopId Id, Rect Rect)> _hits = new();
    private SpatialScene? _displayed;

    /// <summary>Switch to a room (Enter / double-click) — App resolves the id to a jump.</summary>
    public event Action<DesktopId>? JumpRoomRequested;
    /// <summary>Tab — swap back to the row map. App flips the persisted model and re-opens.</summary>
    public event Action? SwapModelRequested;
    /// <summary>A room or block was moved: its new positions are already written to the shared
    /// <see cref="SpatialState"/>; App just persists it to spatial.json.</summary>
    public event Action? PositionsChanged;

    public SpatialOverlay(OverlayStage stage, MapCamera camera)
    {
        _stage = stage;
        _camera = camera;
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCaptureLost += (_, _) => CancelDrag();
    }

    public bool IsOpen => _stage.Current == this;

    /// <summary>Open the spatial map, homing the selection onto the desktop you're on.</summary>
    public void Open(SpatialSource source, SpatialState state)
    {
        _source = source;
        _state = state;
        _initialised = false;
        _stage.Summon(this);
    }

    /// <summary>Stash a fresh scene, preserving the cursor. Redraws now if current.</summary>
    public void SetSource(SpatialSource source, SpatialState state)
    {
        _source = source;
        _state = state;
        if (IsOpen) Render();
    }

    /// <summary>Redraw and re-home the selection onto the desktop you're now on — after a real switch.</summary>
    public void SyncToCurrent(SpatialSource source, SpatialState state)
    {
        if (!IsOpen) return;
        _source = source;
        _state = state;
        _initialised = false;
        Render();
    }

    public void Close()
    {
        if (IsOpen) _stage.Back();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface;
    public bool Durable => true;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage) => Render();
    public void OnRemoved() => _initialised = false;

    public void OnKey(KeyEventArgs e)
    {
        // The move chords come first, with exact-modifier guards, so a plain-arrow case can't swallow them
        // and the global nav chord (Ctrl+Alt+arrow) never reads as a move.
        switch (e.Key)
        {
            case Key.Left when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): MoveBlock(-1, 0); e.Handled = true; return;
            case Key.Right when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): MoveBlock(1, 0); e.Handled = true; return;
            case Key.Up when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): MoveBlock(0, -1); e.Handled = true; return;
            case Key.Down when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): MoveBlock(0, 1); e.Handled = true; return;
            case Key.Left when e.KeyModifiers == KeyModifiers.Control: MoveRoom(-1, 0); e.Handled = true; return;
            case Key.Right when e.KeyModifiers == KeyModifiers.Control: MoveRoom(1, 0); e.Handled = true; return;
            case Key.Up when e.KeyModifiers == KeyModifiers.Control: MoveRoom(0, -1); e.Handled = true; return;
            case Key.Down when e.KeyModifiers == KeyModifiers.Control: MoveRoom(0, 1); e.Handled = true; return;
        }

        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Tab: SwapModelRequested?.Invoke(); e.Handled = true; break;
            case Key.Enter: if (_cursor is { } c) JumpRoomRequested?.Invoke(c); e.Handled = true; break;
            case Key.Left: Nudge(-1, 0); e.Handled = true; break;
            case Key.Right: Nudge(1, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -1); e.Handled = true; break;
            case Key.Down: Nudge(0, 1); e.Handled = true; break;
        }
    }

    // ── Selection ──────────────────────────────────────────────────────────────

    private SpatialScene Scene() => SpatialScene.From(_source, _state);

    // Home the cursor onto the desktop the user is actually on (the source's selected room), else the first.
    private void InitCursor()
    {
        var scene = Scene();
        _cursor = scene.Rooms.FirstOrDefault(r => r.Selected)?.Id
               ?? (scene.Rooms.Count > 0 ? scene.Rooms[0].Id : (DesktopId?)null);
    }

    // Arrow-select: step to the nearest room in the pressed direction, favouring the axis of travel — the
    // 2-D analog of the row map's ←/→ along a row and ↑/↓ between rows.
    private void Nudge(int dx, int dy)
    {
        var rooms = Scene().Rooms;
        if (rooms.Count == 0) return;
        DesktopId curId = _cursor ?? rooms.First().Id;
        SpatialRoom? cur = rooms.FirstOrDefault(r => r.Id == curId);
        if (cur is null) { _cursor = rooms[0].Id; Render(); return; }

        SpatialRoom? best = null;
        int bestScore = int.MaxValue;
        foreach (SpatialRoom r in rooms)
        {
            if (r.Id == curId) continue;
            int ox = r.Pos.X - cur.Pos.X, oy = r.Pos.Y - cur.Pos.Y;
            if (dx != 0 && Math.Sign(ox) != dx) continue;
            if (dy != 0 && Math.Sign(oy) != dy) continue;
            if (dx != 0 && Math.Abs(oy) > Math.Abs(ox)) continue; // keep to the travel axis
            if (dy != 0 && Math.Abs(ox) > Math.Abs(oy)) continue;
            int d = Math.Abs(ox) + Math.Abs(oy);
            if (d < bestScore) { bestScore = d; best = r; }
        }
        if (best is not null) { _cursor = best.Id; Render(); }
    }

    // ── Moving rooms & blocks ──────────────────────────────────────────────────
    // Movement writes grid positions straight into the shared SpatialState (so the change is live) and
    // raises PositionsChanged for App to persist. Effective positions come from the displayed scene, so a
    // never-placed room materialises its default (row-layout) slot the moment it's first moved.

    private void MoveRoom(int dx, int dy)
    {
        if (_cursor is not { } id || RoomOf(id) is not { } room) return;
        _state.SetPosition(id.Value, room.Pos.Offset(dx, dy));
        PositionsChanged?.Invoke();
        Render();
    }

    private void MoveBlock(int dx, int dy)
    {
        if (_cursor is not { } id || RoomOf(id) is null) return;
        foreach (SpatialRoom r in Fragment(id)) _state.SetPosition(r.Id.Value, r.Pos.Offset(dx, dy));
        PositionsChanged?.Invoke();
        Render();
    }

    private SpatialRoom? RoomOf(DesktopId id) => _displayed?.Rooms.FirstOrDefault(r => r.Id == id);

    // The contiguous fragment (touching cells, diagonals included) of the cursor's group that the cursor sits
    // in — the unit a block-move carries, preserving the little arrangement inside it.
    private IReadOnlyList<SpatialRoom> Fragment(DesktopId id)
    {
        if (_displayed is null || RoomOf(id) is not { } cursor) return Array.Empty<SpatialRoom>();
        var members = _displayed.Rooms.Where(r => r.GroupId == cursor.GroupId).ToList();
        var frags = SpatialClusters.Fragments(members.Select(m => m.Pos).ToList());
        int at = members.FindIndex(m => m.Id == id);
        IReadOnlyList<int> frag = frags.First(f => f.Contains(at));
        return frag.Select(i => members[i]).ToList();
    }

    // ── Drag to move ───────────────────────────────────────────────────────────
    // Pointer-driven: a plain drag moves the room, ⇧-drag moves its contiguous block. Positions step by whole
    // cells as the pointer crosses them (cheap — only re-renders on a cell change), and commit on release.

    private const double DragThreshold = 6;
    private bool _tilePressed;                                   // the press bubbling up started on a tile
    private DesktopId? _grab;                                    // the room picked up
    private bool _dragging, _dragBlock;
    private Point _pressAt;
    private (int X, int Y) _appliedDelta;
    private readonly Dictionary<DesktopId, GridPos?> _dragOriginal = new(); // stored pos before the drag (null = was unplaced)
    private readonly Dictionary<DesktopId, GridPos> _dragBase = new();       // effective pos at grab

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        bool onTile = _tilePressed;                             // the tile's own handler ran first, on the way up
        _tilePressed = false;
        CancelDrag();
        if (e.ClickCount >= 2) return;                          // a double-click is "switch", never a drag
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        if (!onTile || _cursor is not { } id || RoomOf(id) is null) return;

        _grab = id;
        _dragBlock = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _pressAt = e.GetPosition(_root);
        _appliedDelta = (0, 0);
        _dragOriginal.Clear();
        _dragBase.Clear();
        foreach (SpatialRoom r in _dragBlock ? Fragment(id) : new[] { RoomOf(id)! })
        {
            _dragBase[r.Id] = r.Pos;
            _dragOriginal[r.Id] = _state.Position(r.Id.Value);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_grab is null) return;
        Point at = e.GetPosition(_root);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(_root);
        }
        (double sx, double sy) = SpatialPainter.Stride(1.0);
        int gx = (int)Math.Round((at.X - _pressAt.X) / sx);
        int gy = (int)Math.Round((at.Y - _pressAt.Y) / sy);
        if ((gx, gy) == _appliedDelta) return;                 // only step on whole-cell crossings
        _appliedDelta = (gx, gy);
        foreach ((DesktopId id, GridPos basePos) in _dragBase) _state.SetPosition(id.Value, basePos.Offset(gx, gy));
        Render();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool moved = _dragging && _appliedDelta != (0, 0);
        e.Pointer.Capture(null);
        _grab = null;
        _dragging = false;
        if (moved) { PositionsChanged?.Invoke(); Render(); }    // positions are already whole cells — just commit
        else CancelDrag();                                     // a click that never dragged: restore any provisional
    }

    private void CancelDrag()
    {
        if (_grab is null && !_dragging) { _dragBase.Clear(); _dragOriginal.Clear(); return; }
        // Roll back any provisional moves to what was stored before the drag (or unplaced).
        foreach ((DesktopId id, GridPos? original) in _dragOriginal)
        {
            if (original is { } p) _state.SetPosition(id.Value, p);
            else _state.ClearPosition(id.Value);
        }
        _grab = null;
        _dragging = false;
        _dragBase.Clear();
        _dragOriginal.Clear();
        if (IsOpen) Render();
    }

    // ── Render ───────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!_initialised) { InitCursor(); _initialised = true; }

        double width = _stage.HostWidth > 0 ? _stage.HostWidth : 1280;
        double height = _stage.HostHeight > 0 ? _stage.HostHeight : 800;

        SpatialScene display = _cursor is { } c
            ? SpatialScene.From(_source, _state, c)
            : SpatialScene.From(_source, _state);
        _displayed = display;

        _hits.Clear();
        Control board = SpatialPainter.Render(display, width, height, 1.0, _camera,
            onClick: id => { _cursor = id; _tilePressed = true; Render(); },
            onActivate: id => JumpRoomRequested?.Invoke(id),
            hits: _hits);

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(BuildLegend());

        _stage.BringToFront();
    }

    private Control BuildLegend()
    {
        var rows = new StackPanel { Spacing = 7 };
        rows.Children.Add(new TextBlock
        {
            Text = "Spatial map", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        });
        rows.Children.Add(LegendRow("←→↑↓", "select the nearest room"));
        rows.Children.Add(LegendRow("Enter", "switch to selected"));
        rows.Children.Add(LegendRow("Ctrl+Alt+←→↑↓", "switch to a desktop"));
        rows.Children.Add(LegendRow("Ctrl+←→↑↓", "move the room"));
        rows.Children.Add(LegendRow("Ctrl+Shift+←→↑↓", "move the block"));
        rows.Children.Add(LegendRow("Tab", "back to the list view"));
        rows.Children.Add(LegendRow("Esc", "close"));
        rows.Children.Add(new TextBlock
        {
            Text = "click to select · double-click to switch", FontSize = 11, Foreground = FgDim,
            Margin = new Avalonia.Thickness(0, 5, 0, 0),
        });
        rows.Children.Add(new TextBlock
        {
            Text = "drag a room · ⇧-drag its block", FontSize = 11, Foreground = FgDim,
        });

        var legend = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new Avalonia.CornerRadius(12), Padding = new Avalonia.Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(24, 24, 0, 0), Child = rows,
        };
        legend.PointerPressed += (_, e) => e.Handled = true; // reading the legend never selects behind it
        return legend;
    }

    private static Control LegendRow(string key, string desc)
    {
        var cap = new Border
        {
            Background = new SolidColorBrush(KeyCapBg),
            CornerRadius = new Avalonia.CornerRadius(5), Padding = new Avalonia.Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent, FontFamily = Mono,
            },
        };
        Grid.SetColumn(cap, 0);
        var label = new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        grid.Children.Add(cap);
        grid.Children.Add(label);
        return grid;
    }
}
