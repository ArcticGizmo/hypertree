using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.App.Views.Scene;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive <b>spatial map</b> — the app's single map and "manage desktops" surface, presented on the
/// shared <see cref="OverlayStage"/>. Desktops are freely-placed rooms; a blue selection cursor moves over a
/// stationary map (arrow keys pick the nearest room in that direction, or click a room), <c>Enter</c> /
/// double-click switches. It shares the app's dead-zone <see cref="MapCamera"/> with the flash, so raising
/// the map never teleports the view.
///
/// Every edit is raised as an event for <c>App</c> (which owns the <see cref="NavigationModel"/> and the
/// desktop controller): navigate, jump, place/move rooms, groups &amp; colours, rename, new desktop/branch,
/// delete, move/pull windows, and the finder/palette/launcher — the overlay never mutates model state.
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
    private DesktopId? _pendingSelect; // where the cursor should land after the current room/group is deleted
    private Guid? _selectedGroup; // the active group (g cycles it); its whole set moves as one
    private bool _groupsPanel;    // the ⇧G groups-and-colours panel is showing
    private Guid? _paletteFor;    // which group's colour palette is expanded in the panel
    private Dictionary<string, GridPos>? _tidyUndo; // positions before the last tidy, for Ctrl+Z
    private bool _initialised;

    // Map zoom (+/−). A whole-map scale applied to the painter's geometry; the camera reframes on each step so
    // the selection stays put rather than sliding off. Stepped multiplicatively and clamped so the map can't
    // shrink to illegibility or blow up past the viewport. Seeded from the persisted preference and raised back
    // on change so App writes it to settings.json — the map reopens at the zoom you left it.
    private double _scale = 1.0;
    private const double MinScale = 0.5, MaxScale = 2.0, ZoomStep = 1.15;

    // The key legend (top-left) is toggleable with `l`. It covers a fair slice of the screen, so power users
    // hide it; the state is seeded from the persisted preference and raised back on change so App writes it to
    // settings.json. When hidden a tiny "l legend" pill keeps the toggle discoverable.
    private bool _legend = true;

    // The last render's per-room host controls (positioned at each room's cell) and the scene behind them, so
    // a drag can move a host directly — no re-render mid-gesture, which would drop the pointer capture.
    private readonly Dictionary<DesktopId, Control> _roomHosts = new();
    private SpatialScene? _displayed;

    // The last render's room hit rects (in root coordinates) and the group the mouse is hovering, so a hover
    // brightens that group's hull — the pointer analog of the blue selection sitting in it.
    private readonly List<(DesktopId Id, Rect Rect)> _hits = new();
    private Guid? _hoverGroup;

    /// <summary>Switch to a room (Enter / double-click) — App resolves the id to a jump.</summary>
    public event Action<DesktopId>? JumpRoomRequested;
    /// <summary>v — cycle the whole-app Map style (Board → Metro → ASCII). App persists it and pushes it back,
    /// so the room glyphs follow.</summary>
    public event Action? ViewStyleToggleRequested;
    /// <summary>A move or a recolour changed the spatial state: it's already written to the shared
    /// <see cref="SpatialState"/>; App just persists it to spatial.json.</summary>
    public event Action? SpatialStateChanged;
    /// <summary>g — set the highlighted room's group. App opens a picker (existing groups + "create «name»")
    /// over the map and reassigns the desktop to the chosen — or newly created — group.</summary>
    public event Action<DesktopId>? SetRoomGroupRequested;
    /// <summary>Del — remove the room (the desktop). App resolves the id and runs its confirm/teardown.</summary>
    public event Action<DesktopId>? DeleteRoomRequested;
    /// <summary>Shift+Del — remove a whole group (a branch). App resolves the group id to a branch.</summary>
    public event Action<Guid>? DeleteGroupRequested;
    /// <summary>r — rename the highlighted room (the desktop). App prompts and relabels.</summary>
    public event Action<DesktopId>? RenameRoomRequested;
    /// <summary>Shift+R — rename the highlighted room's group (a branch). Not raised for main.</summary>
    public event Action<Guid>? RenameGroupRequested;
    /// <summary>n — create a new desktop in the highlighted room's group (branch, or main). App prompts,
    /// creates it, and homes the cursor onto it.</summary>
    public event Action<DesktopId>? NewDesktopRequested;
    /// <summary>b — create a new branch (a new group). App opens the branch card over the map.</summary>
    public event Action? NewBranchRequested;
    /// <summary>m — start the move-windows flow (relocate this desktop's windows elsewhere).</summary>
    public event Action? MoveWindowsRequested;
    /// <summary>Shift+M — start the pull-windows flow (bring windows from other desktops onto this one).</summary>
    public event Action? PullWindowsRequested;
    /// <summary>f — open the finder (jump/create spotlight) over the map; Esc pops back here.</summary>
    public event Action? FinderRequested;
    /// <summary>p — open the command palette over the map; Esc pops back here.</summary>
    public event Action? CommandPaletteRequested;
    /// <summary>o — open the application launcher over the map; Esc pops back here.</summary>
    public event Action? AppLauncherRequested;
    /// <summary>+ / − / 0 — the map zoom changed. Carries the new (clamped) factor; App persists it to
    /// settings.json so the map reopens at the same zoom.</summary>
    public event Action<double>? ZoomChanged;
    /// <summary>l — the legend was shown or hidden. App persists it to settings.json so the map reopens the
    /// way you left it.</summary>
    public event Action<bool>? LegendVisibilityChanged;

    public SpatialOverlay(OverlayStage stage, MapCamera camera, double initialZoom = 1.0, bool showLegend = true)
    {
        _stage = stage;
        _camera = camera;
        _scale = Math.Clamp(initialZoom, MinScale, MaxScale);
        _legend = showLegend;
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCaptureLost += (_, _) => CancelDrag();
        _root.PointerExited += (_, _) => SetHoverGroup(null); // pointer left the map — no group is hovered
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

    /// <summary>The room the cursor is on (for App: where a new branch attaches, which desktop an action
    /// targets). Null until the cursor is homed.</summary>
    public DesktopId? SelectedRoom => _cursor;

    /// <summary>Point the blue selection at a specific room — e.g. a freshly created desktop. Redraws now if
    /// current; else it's held for the next present (stash the scene via <see cref="SetSource"/> first, so
    /// the room exists in the scene being drawn).</summary>
    public void SelectRoom(DesktopId id)
    {
        // A freshly created desktop has no stored position yet: drop it into the empty cell closest to the
        // room it was created from (still the cursor at this point, before we move it onto the new room), so
        // it appears beside its group rather than at a row-layout default that may overlap or scatter.
        if (_displayed is { } scene && _state.Position(id.Value) is null
            && _cursor is { } anchorId && scene.Rooms.FirstOrDefault(r => r.Id == anchorId) is { } anchor)
        {
            var occupied = scene.Rooms.Where(r => r.Id != id).Select(r => r.Pos).ToHashSet();
            _state.SetPosition(id.Value, SpatialPlacement.NearestEmpty(anchor.Pos, occupied));
            SpatialStateChanged?.Invoke(); // persist the placement to spatial.json
        }

        _cursor = id;
        _selectedGroup = null;
        _initialised = true; // keep this selection — don't let InitCursor override it on re-present
        if (IsOpen) Render();
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
            case Key.Enter:
            case Key.Space: if (_cursor is { } c) JumpRoomRequested?.Invoke(c); e.Handled = true; break;
            case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift): ToggleGroupsPanel(); e.Handled = true; break;
            case Key.G: RequestSetGroup(); e.Handled = true; break;
            case Key.V: ViewStyleToggleRequested?.Invoke(); e.Handled = true; break;
            case Key.T: Tidy(); e.Handled = true; break;
            case Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Shift): RequestRenameGroup(); e.Handled = true; break;
            case Key.R: RequestRenameRoom(); e.Handled = true; break;
            case Key.N: RequestNewDesktop(); e.Handled = true; break;
            case Key.B: NewBranchRequested?.Invoke(); e.Handled = true; break;
            case Key.M when e.KeyModifiers.HasFlag(KeyModifiers.Shift): PullWindowsRequested?.Invoke(); e.Handled = true; break;
            case Key.M: MoveWindowsRequested?.Invoke(); e.Handled = true; break;
            case Key.F: FinderRequested?.Invoke(); e.Handled = true; break;
            case Key.P: CommandPaletteRequested?.Invoke(); e.Handled = true; break;
            case Key.O: AppLauncherRequested?.Invoke(); e.Handled = true; break;
            case Key.L: ToggleLegend(); e.Handled = true; break;
            // Zoom. + is usually Shift+= (OemPlus), so accept OemPlus/Add either way; likewise OemMinus/Subtract.
            case Key.Add: case Key.OemPlus: Zoom(ZoomStep); e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / ZoomStep); e.Handled = true; break;
            case Key.D0 when e.KeyModifiers == KeyModifiers.None: ResetZoom(); e.Handled = true; break;
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
    // 2-D analog of the row map's ←/→ along a row and ↑/↓ between rows. Shares the resolver with live
    // navigation, so the map and Ctrl+Alt+Arrow agree on where each arrow lands.
    private void Nudge(int dx, int dy)
    {
        SpatialScene scene = Scene();
        if (scene.Rooms.Count == 0) return;
        DesktopId curId = _cursor ?? scene.Rooms[0].Id;
        if (SpatialNavigation.NextInDirection(scene, curId, dx, dy) is { } next)
        {
            _cursor = next;
            _selectedGroup = null; // plain navigation exits group mode: the room is the active unit again
            Render();
        }
        else if (_cursor is null) { _cursor = scene.Rooms[0].Id; Render(); }
    }

    // ── Groups (set, select, recolour) ─────────────────────────────────────────

    // g — set the highlighted room's group. App owns the picker (existing groups plus a "create «name»"
    // row) and the reassignment; the overlay just hands over which room to move. The ⇧G panel still selects
    // a whole group as the active move unit (Ctrl+arrows / drag).
    private void RequestSetGroup()
    {
        if (_cursor is { } id && RoomOf(id) is not null) SetRoomGroupRequested?.Invoke(id);
    }

    // r / Shift+R / n — desktop and group edits App owns. The overlay hands over the highlighted room (or its
    // group); App prompts, mutates the model, and homes the cursor back via SelectRoom.
    private void RequestRenameRoom()
    {
        if (_cursor is { } id && RoomOf(id) is not null) RenameRoomRequested?.Invoke(id);
    }

    private void RequestRenameGroup()
    {
        // main (the ungrouped bucket) has no branch to rename.
        if (_cursor is { } id && RoomOf(id) is { IsMainGroup: false } room) RenameGroupRequested?.Invoke(room.GroupId);
    }

    private void RequestNewDesktop()
    {
        if (_cursor is { } id && RoomOf(id) is not null) NewDesktopRequested?.Invoke(id);
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

    // ── Zoom (+/−, 0 to reset) ──────────────────────────────────────────────────
    // Scale the whole map, clamped to a legible range. Reframe so the selection recenters at the new scale
    // rather than the carried pixel offset (computed for the old metrics) sliding the view.

    private void Zoom(double factor)
    {
        double next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - _scale) < 1e-6) return; // already at the limit — nothing to redraw
        _scale = next;
        _camera.Reframe();
        Render();
        ZoomChanged?.Invoke(_scale);
    }

    private void ResetZoom()
    {
        if (Math.Abs(_scale - 1.0) < 1e-6) return;
        _scale = 1.0;
        _camera.Reframe();
        Render();
        ZoomChanged?.Invoke(_scale);
    }

    // ── Legend (l) ───────────────────────────────────────────────────────────────
    // Show/hide the key legend. Persisted (via App) so it stays the way you left it; when hidden a small pill
    // keeps the toggle discoverable.

    private void ToggleLegend()
    {
        _legend = !_legend;
        Render();
        LegendVisibilityChanged?.Invoke(_legend);
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
        Persist();
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
        // Pick where the selection lands *before* the room is gone (App's delete is async behind a confirm):
        // the nearest surviving room to the one being removed. Applied by Render once the room disappears.
        if (_cursor is { } id && RoomOf(id) is { } room)
        {
            _pendingSelect = NearestRoom(room.Pos, r => r.Id != id);
            DeleteRoomRequested?.Invoke(id);
        }
    }

    private void DeleteGroup()
    {
        // The group to remove: the selected one, else the cursor's. main (the ungrouped bucket) can't be removed.
        Guid? group = _selectedGroup ?? (_cursor is { } id ? RoomOf(id)?.GroupId : null);
        if (group is { } g && g != Guid.Empty)
        {
            // Land on the nearest room outside the group once its rooms are gone.
            if (_cursor is { } cid && RoomOf(cid) is { } room)
                _pendingSelect = NearestRoom(room.Pos, r => r.GroupId != g);
            DeleteGroupRequested?.Invoke(g);
        }
    }

    // The surviving room nearest grid cell <paramref name="to"/> that passes <paramref name="keep"/> — where
    // the selection should jump when its current room is deleted. Equal distances prefer right, then bottom.
    private DesktopId? NearestRoom(GridPos to, Func<SpatialRoom, bool> keep)
        => _displayed?.Rooms.Where(keep)
            .OrderBy(r => { int dx = r.Pos.X - to.X, dy = r.Pos.Y - to.Y; return dx * dx + dy * dy; })
            .ThenByDescending(r => r.Pos.X).ThenByDescending(r => r.Pos.Y)
            .FirstOrDefault()?.Id;

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
        Persist();
    }

    private void MoveRooms(IReadOnlyList<SpatialRoom> rooms, int dx, int dy)
    {
        if (rooms.Count == 0) return;
        foreach (SpatialRoom r in rooms) _state.SetPosition(r.Id.Value, r.Pos.Offset(dx, dy));
        Persist();
    }

    private void MoveBlock(int dx, int dy)
    {
        if (_cursor is not { } id || RoomOf(id) is null) return;
        foreach (SpatialRoom r in Fragment(id)) _state.SetPosition(r.Id.Value, r.Pos.Offset(dx, dy));
        Persist();
    }

    // Write-through: the move is already in the shared state; tell App to save and redraw. Moves never shove
    // other rooms aside — a landing on an occupied cell just shows an overlap marker (see the painter).
    private void Persist()
    {
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
    // Pointer-driven: a plain drag moves the room, ⇧-drag its contiguous block, or the whole group if one is
    // selected. The room hosts follow the pointer smoothly (no re-render mid-drag, so the pointer capture
    // holds); on release the raw pixel offset snaps to whole cells and the state is written once.

    private const double DragThreshold = 6;
    private bool _tilePressed;                                          // the press bubbling up started on a tile
    private DesktopId? _grab;                                           // the room picked up
    private bool _dragging;
    private Point _pressAt;
    private readonly Dictionary<DesktopId, Point> _dragHostBase = new();   // host screen top-left at grab
    private readonly Dictionary<DesktopId, GridPos> _dragGridBase = new(); // grid position at grab, for the drop

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
        _dragHostBase.Clear();
        _dragGridBase.Clear();
        // What the drag carries: the whole group if this room is in the selected one, else its block on
        // ⇧-drag, else just the room.
        IEnumerable<SpatialRoom> set =
            _selectedGroup is { } g && RoomOf(id)!.GroupId == g ? GroupRooms(g)
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Fragment(id)
            : new[] { RoomOf(id)! };
        foreach (SpatialRoom r in set)
        {
            _dragGridBase[r.Id] = r.Pos;
            if (_roomHosts.TryGetValue(r.Id, out Control? host))
                _dragHostBase[r.Id] = new Point(Canvas.GetLeft(host), Canvas.GetTop(host));
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_grab is null) { UpdateHover(e.GetPosition(_root)); return; }
        Point at = e.GetPosition(_root);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(_root);
        }
        double dx = at.X - _pressAt.X, dy = at.Y - _pressAt.Y;
        // Move the hosts only — the board is not rebuilt, so the capture (and the gesture) survives.
        foreach ((DesktopId id, Point basePt) in _dragHostBase)
            if (_roomHosts.TryGetValue(id, out Control? host))
            {
                Canvas.SetLeft(host, basePt.X + dx);
                Canvas.SetTop(host, basePt.Y + dy);
            }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool wasDragging = _dragging;
        Point at = e.GetPosition(_root);
        var gridBase = new Dictionary<DesktopId, GridPos>(_dragGridBase);
        double dx = at.X - _pressAt.X, dy = at.Y - _pressAt.Y;
        e.Pointer.Capture(null);
        _grab = null;
        _dragging = false;
        _dragHostBase.Clear();
        _dragGridBase.Clear();
        if (!wasDragging) return;                              // a click, not a drag — selection handled on press

        (double sx, double sy) = SpatialPainter.Stride(_scale);
        int gdx = (int)Math.Round(dx / sx), gdy = (int)Math.Round(dy / sy);
        if (gdx == 0 && gdy == 0) { Render(); return; }        // didn't cross a cell — snap the hosts back
        foreach ((DesktopId id, GridPos basePos) in gridBase) _state.SetPosition(id.Value, basePos.Offset(gdx, gdy));
        Persist();
    }

    private void CancelDrag()
    {
        bool wasActive = _grab is not null || _dragging;
        _grab = null;
        _dragging = false;
        _dragHostBase.Clear();
        _dragGridBase.Clear();
        if (wasActive && IsOpen) Render();                     // snap any visually-moved hosts back to their cells
    }

    // Hover: the group whose hull brightens under the mouse. Hit-test the last render's room rects (topmost
    // wins) and, only when the hovered group actually changes, redraw — so a redraw fires at group boundaries,
    // not on every pixel of travel.
    private void UpdateHover(Point at)
    {
        Guid? group = null;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(at)) { group = RoomOf(_hits[i].Id)?.GroupId; break; }
        SetHoverGroup(group);
    }

    private void SetHoverGroup(Guid? group)
    {
        if (group == _hoverGroup || !IsOpen) return;
        _hoverGroup = group;
        Render();
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

        // The selection's room was deleted out from under us (its id no longer resolves): land on the nearest
        // survivor picked when the delete was requested, else the current "here", else the first room — then
        // rebuild so the blue cursor actually shows on the new room instead of vanishing.
        if (_cursor is { } stale && display.Rooms.All(r => r.Id != stale))
        {
            _cursor = _pendingSelect is { } p && display.Rooms.Any(r => r.Id == p) ? p
                    : display.Rooms.FirstOrDefault(r => r.Here)?.Id ?? display.Rooms.FirstOrDefault()?.Id;
            _pendingSelect = null;
            display = _cursor is { } c2 ? SpatialScene.From(_source, _state, c2) : SpatialScene.From(_source, _state);
        }
        _displayed = display;

        _roomHosts.Clear();
        _hits.Clear();
        Control board = SpatialPainter.Render(display, width, height, _scale, _camera,
            onClick: id => { _cursor = id; _selectedGroup = null; _tilePressed = true; Render(); },
            onActivate: id => JumpRoomRequested?.Invoke(id),
            hits: _hits, selectedGroup: _selectedGroup, style: _stage.MapStyle, roomHosts: _roomHosts,
            hoverGroup: _hoverGroup);

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(_legend ? BuildLegend() : BuildLegendHint());
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
        rows.Children.Add(LegendRow("Enter/Space", "switch to selected"));
        rows.Children.Add(LegendRow("Ctrl+Alt+←→↑↓", "switch to a desktop"));
        rows.Children.Add(LegendRow("Ctrl+←→↑↓", "move the room / group"));
        rows.Children.Add(LegendRow("Ctrl+Shift+←→↑↓", "move the block"));
        rows.Children.Add(LegendRow("g", "set the room's group"));
        rows.Children.Add(LegendRow("Shift+g", "groups & colours"));
        rows.Children.Add(LegendRow("r", "rename room"));
        rows.Children.Add(LegendRow("Shift+r", "rename group"));
        rows.Children.Add(LegendRow("n", "new desktop · b new branch"));
        rows.Children.Add(LegendRow("m", "move windows"));
        rows.Children.Add(LegendRow("Shift+m", "pull windows"));
        rows.Children.Add(LegendRow("f", "find · p palette · o apps"));
        rows.Children.Add(LegendRow("t", "tidy up (reunite groups)"));
        rows.Children.Add(LegendRow("+ / −", "zoom in / out · 0 reset"));
        rows.Children.Add(LegendRow("v", _stage.MapStyle switch
        {
            MapStyle.Board => "metro view",
            MapStyle.Metro => "ascii view",
            _ => "board view",
        }));
        rows.Children.Add(LegendRow("Del", "remove room"));
        rows.Children.Add(LegendRow("Shift+Del", "remove group"));
        rows.Children.Add(LegendRow("Ctrl+z", "undo the last tidy"));
        rows.Children.Add(LegendRow("l", "hide this legend"));
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

    // A hint shown in place of the full legend: a small pill in the same corner so the `l` toggle stays
    // discoverable once the legend is hidden. Clicking it never selects the map behind it.
    private Control BuildLegendHint()
    {
        var hint = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new Avalonia.CornerRadius(9), Padding = new Avalonia.Thickness(10, 7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(24, 24, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    KeyCap("l"),
                    new TextBlock { Text = "legend", FontSize = 11, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        hint.PointerPressed += (_, e) => e.Handled = true;
        return hint;
    }

    // A keycap chip — the accent-on-dark rounded label the legend and its hint share.
    private static Control KeyCap(string key) => new Border
    {
        Background = new SolidColorBrush(KeyCapBg),
        CornerRadius = new Avalonia.CornerRadius(5), Padding = new Avalonia.Thickness(7, 2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent, FontFamily = Mono,
        },
    };

    private static Control LegendRow(string key, string desc)
    {
        Control cap = KeyCap(key);
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
