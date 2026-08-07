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
    private Guid? _selectedGroup; // the active group (g cycles it); its whole set moves as one
    private bool _groupsPanel;    // the ⇧G groups-and-colours panel is showing
    private Guid? _paletteFor;    // which group's colour palette is expanded in the panel
    private Dictionary<string, GridPos>? _tidyUndo; // positions before the last tidy, for Ctrl+Z
    private bool _initialised;

    // The last render's room hit-rects (screen space) and scene, so a drag can hit-test and read effective
    // positions without rebuilding.
    private readonly List<(DesktopId Id, Rect Rect)> _hits = new();
    private SpatialScene? _displayed;

    /// <summary>Switch to a room (Enter / double-click) — App resolves the id to a jump.</summary>
    public event Action<DesktopId>? JumpRoomRequested;
    /// <summary>Tab — swap back to the row map. App flips the persisted model and re-opens.</summary>
    public event Action? SwapModelRequested;
    /// <summary>A move or a recolour changed the spatial state: it's already written to the shared
    /// <see cref="SpatialState"/>; App just persists it to spatial.json.</summary>
    public event Action? SpatialStateChanged;
    /// <summary>Del — remove the room (the desktop). App resolves the id and runs its confirm/teardown.</summary>
    public event Action<DesktopId>? DeleteRoomRequested;
    /// <summary>Shift+Del — remove a whole group (a branch). App resolves the group id to a branch.</summary>
    public event Action<Guid>? DeleteGroupRequested;

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
            case Key.Left when e.KeyModifiers == KeyModifiers.Control: MoveActive(-1, 0); e.Handled = true; return;
            case Key.Right when e.KeyModifiers == KeyModifiers.Control: MoveActive(1, 0); e.Handled = true; return;
            case Key.Up when e.KeyModifiers == KeyModifiers.Control: MoveActive(0, -1); e.Handled = true; return;
            case Key.Down when e.KeyModifiers == KeyModifiers.Control: MoveActive(0, 1); e.Handled = true; return;
            case Key.Z when e.KeyModifiers == KeyModifiers.Control: Undo(); e.Handled = true; return;
        }

        switch (e.Key)
        {
            case Key.Escape: OnEscape(); e.Handled = true; break;
            case Key.Tab: SwapModelRequested?.Invoke(); e.Handled = true; break;
            case Key.Enter: if (_cursor is { } c) JumpRoomRequested?.Invoke(c); e.Handled = true; break;
            case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift): ToggleGroupsPanel(); e.Handled = true; break;
            case Key.G: CycleGroup(); e.Handled = true; break;
            case Key.T: Tidy(); e.Handled = true; break;
            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift): DeleteGroup(); e.Handled = true; break;
            case Key.Delete: DeleteCursorRoom(); e.Handled = true; break;
            case Key.Back: DeleteCursorRoom(); e.Handled = true; break;
            case Key.Left: Nudge(-1, 0); e.Handled = true; break;
            case Key.Right: Nudge(1, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -1); e.Handled = true; break;
            case Key.Down: Nudge(0, 1); e.Handled = true; break;
        }
    }

    // Esc peels back one layer at a time: the groups panel, then a group selection, then the map itself.
    private void OnEscape()
    {
        if (_groupsPanel) { _groupsPanel = false; _paletteFor = null; Render(); }
        else if (_selectedGroup is not null) { _selectedGroup = null; Render(); }
        else Close();
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
        // Plain navigation exits group mode: the room is now the active unit again.
        if (best is not null) { _cursor = best.Id; _selectedGroup = null; Render(); }
    }

    // ── Groups (select, cycle, recolour) ───────────────────────────────────────

    // g — step through the groups (main included), lighting the whole set and framing it. While a group is
    // selected it is the active unit: Ctrl+arrows and a drag move it whole.
    private void CycleGroup()
    {
        if (_displayed is null || _displayed.Groups.Count == 0) return;
        var ids = _displayed.Groups.Select(g => g.Id).ToList();
        int at = _selectedGroup is { } g ? ids.IndexOf(g) : -1;
        _selectedGroup = ids[(at + 1) % ids.Count];
        // Frame the group by homing the cursor onto its first room, so the camera keeps it in view.
        if (_displayed.Rooms.FirstOrDefault(r => r.GroupId == _selectedGroup) is { } first) _cursor = first.Id;
        Render();
    }

    private IReadOnlyList<SpatialRoom> GroupRooms(Guid group)
        => _displayed?.Rooms.Where(r => r.GroupId == group).ToList() ?? (IReadOnlyList<SpatialRoom>)Array.Empty<SpatialRoom>();

    private void ToggleGroupsPanel()
    {
        _groupsPanel = !_groupsPanel;
        _paletteFor = null;
        Render();
    }

    private void Recolour(Guid group, string hex)
    {
        _state.SetColor(group, hex);
        _paletteFor = null;
        SpatialStateChanged?.Invoke();
        Render();
    }

    // ── Tidy (t) & undo (Ctrl+Z) ───────────────────────────────────────────────
    // t reunites drifted groups — a selected group on its own, else the whole map — moving each fragment as
    // a rigid block so shapes survive. It snapshots first so Ctrl+Z puts everything back.

    private void Tidy()
    {
        if (_displayed is null) return;
        _tidyUndo = new Dictionary<string, GridPos>(_state.Positions);  // snapshot for a one-step undo
        IReadOnlyDictionary<DesktopId, GridPos> moves = _selectedGroup is { } g
            ? SpatialTidy.Group(_displayed, g)
            : SpatialTidy.All(_displayed);
        foreach ((DesktopId id, GridPos pos) in moves) _state.SetPosition(id.Value, pos);
        // Tidying one group can push its block onto another group's cells; resolve that (All never overlaps).
        Commit(moves.Keys.ToHashSet());
    }

    private void Undo()
    {
        if (_tidyUndo is null) return;
        _state.Positions = new Dictionary<string, GridPos>(_tidyUndo);  // restore the pre-tidy layout
        _tidyUndo = null;
        SpatialStateChanged?.Invoke();
        Render();
    }

    // ── Delete (Del / Shift+Del) ────────────────────────────────────────────────
    // Deleting is a real desktop teardown, so it goes through App (confirm + destroy). Spatially the hole
    // just stays — positions are independent, so no neighbour reflows.

    private void DeleteCursorRoom()
    {
        if (_cursor is { } id && RoomOf(id) is not null) DeleteRoomRequested?.Invoke(id);
    }

    private void DeleteGroup()
    {
        // The group to remove: the selected one, else the cursor's. main (the ungrouped bucket) can't be removed.
        Guid? group = _selectedGroup ?? (_cursor is { } id ? RoomOf(id)?.GroupId : null);
        if (group is { } g && g != Guid.Empty) DeleteGroupRequested?.Invoke(g);
    }

    // ── Moving rooms, blocks & groups ──────────────────────────────────────────
    // Movement writes grid positions straight into the shared SpatialState (so the change is live) and
    // raises SpatialStateChanged for App to persist. Effective positions come from the displayed scene, so a
    // never-placed room materialises its default (row-layout) slot the moment it's first moved.

    // Ctrl+arrows: move the active unit — the whole selected group if one is selected, else the single room.
    private void MoveActive(int dx, int dy)
    {
        if (_selectedGroup is { } g) MoveRooms(GroupRooms(g), dx, dy);
        else MoveRoom(dx, dy);
    }

    private void MoveRoom(int dx, int dy)
    {
        if (_cursor is not { } id || RoomOf(id) is not { } room) return;
        _state.SetPosition(id.Value, room.Pos.Offset(dx, dy));
        Commit(new HashSet<DesktopId> { id });
    }

    private void MoveRooms(IReadOnlyList<SpatialRoom> rooms, int dx, int dy)
    {
        if (rooms.Count == 0) return;
        foreach (SpatialRoom r in rooms) _state.SetPosition(r.Id.Value, r.Pos.Offset(dx, dy));
        Commit(rooms.Select(r => r.Id).ToHashSet());
    }

    // Finish a move: the moved rooms have their new positions in the state; bump any rooms they landed on to
    // the nearest free cell (no invisible stacking), then persist and redraw.
    private void Commit(IReadOnlySet<DesktopId> moved)
    {
        SpatialScene scene = Scene();
        var fixes = SpatialPlacement.ResolveOverlaps(scene.Rooms.Select(r => (r.Id, r.Pos)).ToList(), moved);
        foreach ((DesktopId id, GridPos pos) in fixes) _state.SetPosition(id.Value, pos);
        SpatialStateChanged?.Invoke();
        Render();
    }

    private void MoveBlock(int dx, int dy)
    {
        if (_cursor is not { } id || RoomOf(id) is null) return;
        foreach (SpatialRoom r in Fragment(id)) _state.SetPosition(r.Id.Value, r.Pos.Offset(dx, dy));
        SpatialStateChanged?.Invoke();
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
    private bool _dragging;
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
        _pressAt = e.GetPosition(_root);
        _appliedDelta = (0, 0);
        _dragOriginal.Clear();
        _dragBase.Clear();
        // What the drag carries: the whole group if this room is in the selected one, else its block on
        // ⇧-drag, else just the room.
        IEnumerable<SpatialRoom> set =
            _selectedGroup is { } g && RoomOf(id)!.GroupId == g ? GroupRooms(g)
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Fragment(id)
            : new[] { RoomOf(id)! };
        foreach (SpatialRoom r in set)
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
        var movedIds = new HashSet<DesktopId>(_dragBase.Keys);
        e.Pointer.Capture(null);
        if (moved)                                             // positions are whole cells already
        {
            _grab = null; _dragging = false;
            _dragBase.Clear(); _dragOriginal.Clear();
            Commit(movedIds);                                  // bump anything the drop landed on, persist, redraw
        }
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
            onClick: id => { _cursor = id; _selectedGroup = null; _tilePressed = true; Render(); },
            onActivate: id => JumpRoomRequested?.Invoke(id),
            hits: _hits, selectedGroup: _selectedGroup);

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(BuildLegend());
        if (_groupsPanel) _root.Children.Add(BuildGroupsPanel(display));

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
        rows.Children.Add(LegendRow("Ctrl+←→↑↓", "move the room / group"));
        rows.Children.Add(LegendRow("Ctrl+Shift+←→↑↓", "move the block"));
        rows.Children.Add(LegendRow("g", "select a group"));
        rows.Children.Add(LegendRow("Shift+g", "groups & colours"));
        rows.Children.Add(LegendRow("t", "tidy up (reunite groups)"));
        rows.Children.Add(LegendRow("Del", "remove room · Shift+Del group"));
        rows.Children.Add(LegendRow("Ctrl+z", "undo the last tidy"));
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

    // ── Groups & colours panel (top-right, ⇧G) ─────────────────────────────────

    private Control BuildGroupsPanel(SpatialScene scene)
    {
        var rows = new StackPanel { Spacing = 4, MinWidth = 210 };
        rows.Children.Add(new TextBlock
        {
            Text = "Groups", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
        });
        rows.Children.Add(new TextBlock
        {
            Text = "click a swatch to recolour — colours are stable", FontSize = 10.5, Foreground = FgDim,
            Margin = new Avalonia.Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        foreach (SpatialGroup g in scene.Groups)
        {
            rows.Children.Add(GroupRow(g, scene.Rooms.Count(r => r.GroupId == g.Id)));
            if (_paletteFor == g.Id && !g.IsMain) rows.Children.Add(PaletteRow(g.Id));
        }

        var panel = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new Avalonia.CornerRadius(12), Padding = new Avalonia.Thickness(14, 12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 24, 24, 0), Child = rows,
        };
        panel.PointerPressed += (_, e) => e.Handled = true; // operating the panel never drags/deselects behind it
        return panel;
    }

    private Control GroupRow(SpatialGroup g, int count)
    {
        Color c = Color.Parse(g.Color);
        var swatch = new Border
        {
            Width = 15, Height = 15, Background = new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(g.IsMain ? 8 : 4), // main reads as the round "default" chip
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = g.IsMain ? null : new Cursor(StandardCursorType.Hand),
        };
        Guid id = g.Id;
        if (!g.IsMain)
            swatch.PointerPressed += (_, e) => { e.Handled = true; _paletteFor = _paletteFor == id ? null : id; Render(); };

        var name = new TextBlock
        {
            Text = g.IsMain ? "main" : g.Name, FontFamily = Mono, FontSize = 11.5,
            Foreground = new SolidColorBrush(g.IsMain ? Color.Parse("#9AA6B8") : c),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(9, 0, 0, 0),
        };
        var tally = new TextBlock
        {
            Text = count.ToString(), FontFamily = Mono, FontSize = 11, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(swatch, 0); Grid.SetColumn(name, 1); Grid.SetColumn(tally, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(swatch); grid.Children.Add(name); grid.Children.Add(tally);

        var row = new Border
        {
            Padding = new Avalonia.Thickness(6, 5), CornerRadius = new Avalonia.CornerRadius(7),
            Background = _selectedGroup == id ? new SolidColorBrush(Color.FromArgb(0x1F, 0x6E, 0xA8, 0xFF)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = grid,
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.Handled) return; // the swatch was clicked
            e.Handled = true;
            _selectedGroup = id;
            if (Scene().Rooms.FirstOrDefault(r => r.GroupId == id) is { } first) _cursor = first.Id;
            Render();
        };
        return row;
    }

    private Control PaletteRow(Guid group)
    {
        var wrap = new WrapPanel { Margin = new Avalonia.Thickness(24, 2, 0, 6) };
        foreach (string hex in SpatialPalette.Colors)
        {
            string h = hex;
            var chip = new Border
            {
                Width = 17, Height = 17, Margin = new Avalonia.Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.Parse(h)), CornerRadius = new Avalonia.CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), BorderThickness = new Avalonia.Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            chip.PointerPressed += (_, e) => { e.Handled = true; Recolour(group, h); };
            wrap.Children.Add(chip);
        }
        return wrap;
    }
}
