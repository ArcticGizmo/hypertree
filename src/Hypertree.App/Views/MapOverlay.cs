using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.App.Views.Scene;
using Hypertree.Layout;
using Hypertree.Scopes;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive map — and the app's single "manage desktops" surface — presented on the shared
/// <see cref="OverlayStage"/>. Renders the full board on the primary monitor over the stage's dim
/// backdrop and drives a <b>selection cursor</b> that moves purely visually: the arrow keys (or a single
/// click) point at any desktop <b>without switching to it</b>, so you can inspect and manage the whole
/// layout from wherever you are. <b>↑/↓</b> step between rows and land on each row's <em>own</em> desktop —
/// where the selection last sat in it, or its resume point — the same re-entry a real switch does, rather
/// than dragging a column across rows of different lengths. The selected tile carries the blue focus
/// outline; the desktop you're actually on keeps the green "here" marker (so the two never blur). Ctrl+Alt+Arrow still switches
/// desktops (handled by <c>App</c>) and re-homes the selection onto the desktop you land on; a
/// double-click does the same for a specific tile.
///
/// A shortcut legend in the top-left lists the management actions, each raised as an event for <c>App</c>
/// (which owns the <see cref="NavigationModel"/> and desktop controller): <b>r</b> rename, <b>Shift+r</b>
/// rename the branch (no-op on main), <b>Del</b>
/// delete desktop, <b>Shift+Del</b> delete branch, <b>n</b> new desktop in the selected row, <b>b</b> new branch, <b>m</b>
/// move this desktop's windows elsewhere, <b>f</b> the finder, <b>p</b> the command palette. The last two
/// open <em>over</em> the map, so Esc pops back to it. Because it lives on the persistent stage it survives the desktop
/// switches of navigation (the stage is pinned to every desktop). Closes on Esc, a backdrop click on
/// another monitor, or toggling it off.
///
/// The map is also where the layout gets <b>rearranged</b>, since it's the only surface that shows the
/// whole stack: <b>Shift+↑/↓</b> re-slots the selected branch in the vertical stack (crossing main just
/// re-slots main), and <b>Ctrl+arrows</b> move the selected desktop along its row or into the row
/// above/below — main ↔ branch. The same two moves are available by dragging: a tile by its face, a branch
/// by its box, each showing an accent separator where it would drop — between two tiles for a desktop,
/// between two rows for a branch. Both resolve against the <see cref="BoardLayout"/> the last render
/// reported, and both are raised for <c>App</c> to apply to the model — the map never mutates state itself.
/// </summary>
internal sealed class MapOverlay : IStageContent
{
    private readonly OverlayStage _stage;

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly Color LegendBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22);
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A);
    private static readonly Color DragScrim = Color.FromArgb(0x9E, 0x0E, 0x12, 0x1A);

    private readonly Grid _root = new();
    // The shared dead-zone camera (owned by App, also driving the flash): the cursor moves over a stationary
    // map, and the map pans only when the selection nears an edge. Persists across renders — its "don't move
    // unless needed" state is the point — and across the flash, so the two stay in step. App reframes it on a
    // theme switch. See docs/design/scene-camera.md.
    private readonly MapCamera _camera;
    private NavMap _base = new(Array.Empty<NavMapTile>(), 0, true, Array.Empty<NavMapBranch>());
    private bool _initialised;
    private int _row; // index into the combined row sequence (branches split around main)
    private int _col; // index within the current row
    // Where the selection last sat in each row, so stepping up/down back into a row returns to that desktop
    // rather than carrying the column across. Keyed by branch index (-1 = main); seeded from the model's own
    // resume points, then kept current as the selection browses. See MoveRow.
    private readonly Dictionary<int, int> _colOfRow = new();

    /// <summary>Double-click / activate a desktop — jump there. Top-row index, or branch index + desktop.</summary>
    public event Action<int>? JumpTopRequested;
    public event Action<int, int>? JumpBranchRequested;
    /// <summary>Delete a desktop — the selected one (Del) or a clicked × badge.</summary>
    public event Action<DesktopSelection>? DeleteDesktopRequested;
    /// <summary>Delete an entire branch (Shift+Del) by its index.</summary>
    public event Action<int>? DeleteBranchRequested;
    /// <summary>Re-slot a branch (Shift+↑/↓, or a dragged branch box): its index, and the row of the
    /// combined sequence it should end up on — branches above main, main, then the branches below.</summary>
    public event Action<int, int>? MoveBranchRequested;
    /// <summary>Re-slot the main timeline (Shift+↑/↓ with main selected): the row of the combined sequence
    /// it should end up on.</summary>
    public event Action<int>? MoveMainRequested;
    /// <summary>Move a desktop to another slot (Ctrl+arrows, or a dragged tile): where it is now, and where
    /// it should land. The destination's <c>DesktopIndex</c> is an <em>insertion point</em> in its row,
    /// counting the desktop itself when it isn't leaving that row.</summary>
    public event Action<DesktopSelection, DesktopSelection>? MoveDesktopRequested;
    /// <summary>Rename the selected desktop (r).</summary>
    public event Action<DesktopSelection>? RenameRequested;
    /// <summary>Rename the selected branch (Shift+R) by its index. No-op on main.</summary>
    public event Action<int>? RenameBranchRequested;
    /// <summary>Create a new desktop (n) — in the selected row, so it lands in the branch you're looking at
    /// (or on main when that's what's selected) — / a new branch (b).</summary>
    public event Action<DesktopSelection>? NewDesktopRequested;
    public event Action? NewBranchRequested;
    /// <summary>Start the move-windows flow (m) — relocate this desktop's windows to another.</summary>
    public event Action? MoveWindowsRequested;
    /// <summary>Start the pull-windows flow (Shift+m) — bring windows from other desktops onto this one.</summary>
    public event Action? PullWindowsRequested;
    /// <summary>f / Ctrl+F — open the finder (jump/create spotlight) from the map.</summary>
    public event Action? FinderRequested;
    /// <summary>p — open the command palette over the map, so Esc pops back here.</summary>
    public event Action? CommandPaletteRequested;
    /// <summary>o — open the application launcher over the map, so Esc pops back here.</summary>
    public event Action? AppLauncherRequested;
    /// <summary>v — flip the whole-app board style (board ↔ metro). App owns the setting: it persists the
    /// change and pushes the new style back onto the stage, so every surface follows, not just this map.</summary>
    public event Action? ViewStyleToggleRequested;
    /// <summary>Tab — swap to the spatial map model. App flips the persisted <c>MapModel</c> and re-opens the
    /// map in the other model, sharing the camera so the view doesn't teleport.</summary>
    public event Action? SwapModelRequested;

    public MapOverlay(OverlayStage stage, MapCamera camera)
    {
        _stage = stage;
        _camera = camera;
        // Drag-to-rearrange. The tiles' own handlers select/activate without marking the press handled, so
        // it bubbles up here and a press is both "select this" and "maybe start dragging it".
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCaptureLost += (_, _) => CancelDrag();
    }

    public bool IsOpen => _stage.Current == this;

    /// <summary>Supplies the breadcrumb trail for the top-right history panel (owned by App, which holds
    /// the <c>NavHistory</c>). Null / empty hides the panel.</summary>
    public Func<IReadOnlyList<HistoryCrumb>>? HistoryProvider { get; set; }

    /// <summary>The desktop the map currently has selected (for App: e.g. where a new branch should attach).</summary>
    public DesktopSelection Selection => CurrentSelection();

    /// <summary>Open the map, homing the selection onto the desktop you're currently on. A fresh root —
    /// the map is the durable base other surfaces open over and return to.</summary>
    public void Open(NavMap map)
    {
        _base = map;
        _initialised = false;
        _colOfRow.Clear(); // a fresh map session re-seeds each row from the model's resume points
        // The camera is shared with the flash and persists deliberately, so opening doesn't reframe: it shows
        // wherever navigation left it, with the current selection kept in view by the dead zone.
        _stage.Summon(this);
    }

    /// <summary>Stash a fresh board to show. Redraws now if the map is current; otherwise it's held and
    /// applied the next time the map is (re)presented — e.g. after an action completes on a card and the
    /// stage unwinds back to the map. Selection is preserved across the swap.</summary>
    public void SetBoard(NavMap map)
    {
        _base = map;
        if (IsOpen) Render();
    }

    /// <summary>Redraw and re-home the selection onto the desktop you're now on — after a real switch
    /// (Ctrl+Alt+Arrow or a double-click jump), so the blue selection rejoins the green "here" marker.</summary>
    public void SyncToCurrent(NavMap map)
    {
        if (!IsOpen) return;
        _base = map;
        _initialised = false;
        Render();
    }

    /// <summary>Point the selection at a specific desktop (e.g. a freshly created one). Redraws now if the
    /// map is current; otherwise it's held for the next present (set the board via <see cref="SetBoard"/>
    /// first, so the row/column resolve against the new layout).</summary>
    public void Select(DesktopSelection sel)
    {
        if (sel.OnMain) { _row = Split; _col = sel.DesktopIndex; }
        else { _row = RowOfBranch(sel.BranchIndex); _col = sel.DesktopIndex; }
        _initialised = true; // keep this selection — don't let InitSelection override it on re-present
        if (IsOpen) Render();
    }

    public void Close()
    {
        if (IsOpen) _stage.Back();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface; // draws its own board over the stage's dim
    public bool Durable => true;              // the base surfaces open over and completed actions return to
    public bool DismissOnDeactivate => false; // must survive the deactivation a desktop switch / dialog causes
    public bool DismissOnClickAway => false;  // clicking the primary board never closes; Esc / dim click do

    public void OnPresented(OverlayStage stage) => Render();
    public void OnRemoved() { _initialised = false; CancelDrag(); }

    public void OnKey(KeyEventArgs e)
    {
        // The rearrange chords come first: a plain-arrow case would otherwise swallow them (it matches on
        // the key alone). Exact-modifier guards keep the nav chord (Ctrl+Alt+arrow, handled globally by
        // App) from ever reading as a rearrange if it does reach us.
        switch (e.Key)
        {
            case Key.Up when e.KeyModifiers == KeyModifiers.Shift: MoveBranchRow(-1); e.Handled = true; break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Shift: MoveBranchRow(+1); e.Handled = true; break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Control: MoveDesktopRow(-1); e.Handled = true; break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Control: MoveDesktopRow(+1); e.Handled = true; break;
            case Key.Left when e.KeyModifiers == KeyModifiers.Control: MoveDesktopAlongRow(-1); e.Handled = true; break;
            case Key.Right when e.KeyModifiers == KeyModifiers.Control: MoveDesktopAlongRow(+1); e.Handled = true; break;

            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Tab: SwapModelRequested?.Invoke(); e.Handled = true; break;
            case Key.Enter: JumpToSelection(); e.Handled = true; break;
            case Key.Left: MoveCol(-1); e.Handled = true; break;
            case Key.Right: MoveCol(+1); e.Handled = true; break;
            case Key.Up: MoveRow(-1); e.Handled = true; break;
            case Key.Down: MoveRow(+1); e.Handled = true; break;
            // The management actions are all bare letters while the map has focus, so the two surfaces you
            // can open from here get one too: f is Ctrl+F, p is the command palette (which Ctrl+Alt+P also
            // routes here while the map is up, so Esc pops back to the map rather than closing it).
            case Key.F: FinderRequested?.Invoke(); e.Handled = true; break;
            case Key.P: CommandPaletteRequested?.Invoke(); e.Handled = true; break;
            case Key.O: AppLauncherRequested?.Invoke(); e.Handled = true; break;
            case Key.V: ViewStyleToggleRequested?.Invoke(); e.Handled = true; break;
            case Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (!RowIsMain(_row)) RenameBranchRequested?.Invoke(BranchOfRow(_row)); // no branch to rename on main
                e.Handled = true;
                break;
            case Key.R: RenameRequested?.Invoke(CurrentSelection()); e.Handled = true; break;
            case Key.N: NewDesktopRequested?.Invoke(CurrentSelection()); e.Handled = true; break;
            case Key.B: NewBranchRequested?.Invoke(); e.Handled = true; break;
            case Key.M when e.KeyModifiers.HasFlag(KeyModifiers.Shift): PullWindowsRequested?.Invoke(); e.Handled = true; break;
            case Key.M: MoveWindowsRequested?.Invoke(); e.Handled = true; break;
            case Key.Delete:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (!RowIsMain(_row)) DeleteBranchRequested?.Invoke(BranchOfRow(_row)); // no branch to delete on main
                }
                else DeleteDesktopRequested?.Invoke(CurrentSelection());
                e.Handled = true;
                break;
        }
    }

    // ── Selection over the combined row sequence ───────────────────────────────────
    // Same layout as BoardView: branches[0..Split-1] / main / branches[Split..]. Rows run 0..BranchCount,
    // with main occupying row `Split`. The cursor walks this sequence without touching the model.

    private int Split => Math.Clamp(_base.TopPosition, 0, _base.Branches.Count);
    private int RowCount => _base.Branches.Count + 1;
    private bool RowIsMain(int row) => row == Split;
    private int BranchOfRow(int row) => row < Split ? row : row - 1;
    private int RowOfBranch(int branchIndex) => branchIndex < Split ? branchIndex : branchIndex + 1;
    private int TilesInRow(int row)
        => RowIsMain(row) ? _base.TopRow.Count : _base.Branches[BranchOfRow(row)].Desktops.Count;

    // Key a row for the remembered-column table by the thing that identifies it in the model (a branch, or
    // main), not by its row number — main's slot and a branch's row both move as the stack is rearranged.
    private int KeyOfRow(int row) => RowIsMain(row) ? -1 : BranchOfRow(row);

    // The column to land on when the selection steps into `row`: where it last sat in that row this session,
    // else the row's own resume point from the model (a branch's last-used desktop, or main's cursor) — the
    // same column the row is already drawn centred on, so stepping into it doesn't move the board.
    private int RememberedCol(int row)
        => RowIsMain(row)
            ? ColumnIn(-1, _base.TopCursor, _base.TopRow.Count)
            : ColumnIn(BranchOfRow(row), _base.Branches[BranchOfRow(row)].Cursor, TilesInRow(row));

    // ↑/↓ — step to the row above/below, landing on that row's own desktop rather than keeping the column.
    // Rows are different lengths, so carrying the column would collapse it to 0 the moment it crossed a
    // shorter row (a one-desktop branch) and every row below would then read as "first desktop"; resuming
    // per row is also what a real switch does (NavigationModel enters a branch at its LastUsedIndex).
    private void MoveRow(int delta)
    {
        int row = Math.Clamp(_row + delta, 0, RowCount - 1);
        if (row == _row) return;
        _row = row;
        _col = RememberedCol(row);
        Render(); // clamps, then records this row's column
    }

    // ←/→ — move along the current row.
    private void MoveCol(int delta)
    {
        _col = Math.Clamp(_col + delta, 0, Math.Max(0, TilesInRow(_row) - 1));
        Render();
    }

    private DesktopSelection CurrentSelection()
        => RowIsMain(_row)
            ? new DesktopSelection(true, -1, _col)
            : new DesktopSelection(false, BranchOfRow(_row), _col);

    // Enter switches to the selected desktop (same as a double-click / Ctrl+Alt+Arrow) — App jumps and
    // re-homes the selection onto it.
    private void JumpToSelection()
    {
        if (RowIsMain(_row)) JumpTopRequested?.Invoke(_col);
        else JumpBranchRequested?.Invoke(BranchOfRow(_row), _col);
    }

    // A single click points the selection at the clicked tile (no switch); a double click jumps (raised
    // to App). onTop/onBranch mirror BoardView's tile callbacks. Only a tile press reaches these, so they
    // double as "the press that's bubbling up landed on a tile" — see OnPointerPressed.
    private void SelectTop(int index) { _tilePressed = true; _row = Split; _col = index; Render(); }
    private void SelectBranch(int branchIndex, int desktopIndex)
    {
        _tilePressed = true;
        _row = RowOfBranch(branchIndex);
        _col = desktopIndex;
        Render();
    }

    // ── Rearranging the layout (Shift/Ctrl+arrows, and the drop half of a drag) ─────

    // Shift+↑/↓ — lift the selected row one place up or down the stack. A branch swaps with whatever it
    // steps over (main included); main itself re-slots the same way, moving branches above it to below.
    // App applies it and re-homes the selection on the row that moved.
    private void MoveBranchRow(int delta)
    {
        int target = _row + delta;
        if (target < 0 || target >= RowCount) return;
        if (RowIsMain(_row)) MoveMainRequested?.Invoke(target);
        else MoveBranchRequested?.Invoke(BranchOfRow(_row), target);
    }

    // Ctrl+↑/↓ — move the selected desktop into the row above/below (branch ↔ main ↔ branch), keeping it
    // at roughly the same column.
    private void MoveDesktopRow(int delta)
    {
        int target = _row + delta;
        if (target < 0 || target >= RowCount) return;
        RequestDesktopMove(CurrentSelection(), target, Math.Min(_col, TilesInRow(target)));
    }

    // Ctrl+←/→ — slide the selected desktop one place along its own row. Insertion points count the
    // desktop itself, so stepping right is "insert two along" (past its own slot and its neighbour's).
    private void MoveDesktopAlongRow(int delta)
    {
        int insertAt = delta > 0 ? _col + 2 : _col - 1;
        if (insertAt < 0 || insertAt > TilesInRow(_row)) return;
        RequestDesktopMove(CurrentSelection(), _row, insertAt);
    }

    private void RequestDesktopMove(DesktopSelection from, int toRow, int insertAt)
    {
        DesktopSelection to = RowIsMain(toRow)
            ? new DesktopSelection(true, -1, insertAt)
            : new DesktopSelection(false, BranchOfRow(toRow), insertAt);
        MoveDesktopRequested?.Invoke(from, to);
    }

    // ── Drag to rearrange ──────────────────────────────────────────────────────────
    // Pointer-driven rather than Avalonia's DragDrop: the board is an absolutely-positioned canvas the map
    // rebuilds wholesale, so there are no durable drop targets to register — but the render hands back a
    // BoardLayout, and hit-testing that is both exact and cheap.

    private const double DragThreshold = 6; // px of travel before a press becomes a drag (not a click)

    private enum Grab { None, Desktop, Branch }

    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private BoardLayout? _layout;          // where the last render put everything
    private bool _tilePressed;             // the press bubbling up to us started on a tile
    private Grab _grab;                    // what the current press picked up (None once released)
    private bool _dragging;                // past the threshold — indicators show and a drop will commit
    private DesktopSelection _grabbedTile;
    private int _grabbedBranch = -1;
    private Point _pressAt, _pointerAt;
    private int _dropRow = -1;             // desktop drags: the row a drop would land on
    private int _dropIndex;                // desktop drags: the insertion point within _dropRow
    private int _dropBoundary = -1;        // branch drags: the row boundary a drop would slot into

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        bool onTile = _tilePressed; // the tile's own handler ran first, on the way up to here
        _tilePressed = false;
        CancelDrag();
        if (e.ClickCount >= 2) return; // a double-click is "jump to this desktop", never a drag
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;

        _pressAt = _pointerAt = e.GetPosition(_root);
        if (onTile)
        {
            // Take the grabbed tile from the selection the tile's click just set, not from a fresh
            // hit-test: selecting re-centres the board on that tile, so by the time the press reaches us
            // the layout has moved out from under the pointer.
            _grab = Grab.Desktop;
            _grabbedTile = CurrentSelection();
        }
        else if (RowContaining(_pressAt) is { IsMain: false } row)
        {
            // Anywhere in a branch's box that isn't a tile — its label, its padding — is the branch's own
            // drag handle. Main has no box, so it can't be dragged (it's the pivot the stack splits around).
            _grab = Grab.Branch;
            _grabbedBranch = row.BranchIndex;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_grab == Grab.None) return;
        _pointerAt = e.GetPosition(_root);
        if (!_dragging)
        {
            if (Math.Abs(_pointerAt.X - _pressAt.X) < DragThreshold &&
                Math.Abs(_pointerAt.Y - _pressAt.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(_root); // keep the moves coming even once the pointer leaves the board
        }
        ResolveDrop(_pointerAt);
        RenderDragLayer();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        (Grab grab, bool dragging) = (_grab, _dragging);
        (int row, int index, int boundary) = (_dropRow, _dropIndex, _dropBoundary);
        (DesktopSelection tile, int branch) = (_grabbedTile, _grabbedBranch);
        e.Pointer.Capture(null);
        CancelDrag();
        if (!dragging) return;

        // Raise the move last: App re-renders the map from the model, and the drag state is already clear.
        if (grab == Grab.Desktop && row >= 0) RequestDesktopMove(tile, row, index);
        else if (grab == Grab.Branch && branch >= 0 && boundary >= 0)
        {
            // A boundary counts the branch's own row, so slotting in below itself is one row lower than the
            // boundary's number once the branch is lifted out — the same shift a desktop's insertion point has.
            int at = RowOfBranch(branch);
            MoveBranchRequested?.Invoke(branch, boundary > at ? boundary - 1 : boundary);
        }
    }

    private void CancelDrag()
    {
        if (_grab == Grab.None && !_dragging) return;
        _grab = Grab.None;
        _dragging = false;
        _grabbedBranch = -1;
        _dropRow = -1;
        _dropBoundary = -1;
        _dragLayer.Children.Clear();
    }

    private BoardRow? RowContaining(Point p)
    {
        if (_layout is null) return null;
        foreach (BoardRow r in _layout.Rows) if (r.Bounds.Contains(p)) return r;
        return null;
    }

    // Where would a drop here land? A desktop goes *into* a row, so it resolves to the nearest row (dropping
    // in the gap between two still counts) plus the nearest tile boundary within it. A branch is a row, so it
    // resolves to the nearest boundary *between* rows instead — the separator the drag draws.
    private void ResolveDrop(Point p)
    {
        _dropRow = -1;
        _dropBoundary = -1;
        if (_layout is null || _layout.Rows.Count == 0) return;

        if (_grab == Grab.Branch) { _dropBoundary = _layout.NearestBoundary(p.Y); return; }

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _layout.Rows.Count; i++)
        {
            double d = _layout.Rows[i].VerticalDistanceTo(p.Y);
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        _dropRow = best;
        _dropIndex = _layout.Rows[best].InsertIndexAt(p.X);
    }

    // The drag feedback, in its own layer over the board: a scrim on what you picked up, a separator where it
    // would drop — vertical between two tiles, horizontal between two rows — and a chip on the pointer. Kept
    // separate so following the pointer never re-renders the board underneath.
    private void RenderDragLayer()
    {
        _dragLayer.Children.Clear();
        if (!_dragging || _layout is null) return;

        if (_grab == Grab.Desktop)
        {
            if (TileRectOf(_grabbedTile) is { } src) Add(Scrim(src), src.X, src.Y);
            if (_dropRow >= 0 && _dropRow < _layout.Rows.Count)
            {
                BoardRow row = _layout.Rows[_dropRow];
                Add(Separator(3, row.TileHeight + 8), row.BoundaryX(_dropIndex) - 1.5, row.TileTop - 4);
            }
            Add(Chip(LabelOf(_grabbedTile)), _pointerAt.X + 14, _pointerAt.Y + 14);
        }
        else if (_grab == Grab.Branch)
        {
            if (BandOfBranch(_grabbedBranch) is { } src) Add(Scrim(src.Bounds), src.Bounds.X, src.Bounds.Y);
            if (_dropBoundary >= 0)
            {
                // The same insertion-line idea turned on its side: a branch is a row, so it slots between two
                // rows rather than into one.
                (double left, double right) = _layout.BoundarySpan(_dropBoundary);
                Add(Separator(right - left, 3), left, _layout.BoundaryY(_dropBoundary) - 1.5);
            }
            Add(Chip("● " + (BranchName(_grabbedBranch) ?? "branch")), _pointerAt.X + 14, _pointerAt.Y + 14);
        }
    }

    private static Control Separator(double width, double height) => new Rectangle
    {
        Width = width, Height = height, RadiusX = 2, RadiusY = 2, Fill = Accent,
    };

    private void Add(Control c, double left, double top)
    {
        Canvas.SetLeft(c, left);
        Canvas.SetTop(c, top);
        _dragLayer.Children.Add(c);
    }

    // What you picked up reads as "lifted out": veiled in place, so the caret and the chip are the live
    // part of the drag.
    private static Control Scrim(Rect r) => new Border
    {
        Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(DragScrim),
    };

    private static Control Chip(string text) => new Border
    {
        Background = new SolidColorBrush(KeyCapBg), BorderBrush = Accent, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 4),
        Child = new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Fg,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        },
    };

    private Rect? TileRectOf(DesktopSelection sel)
    {
        if (_layout is null) return null;
        foreach (BoardTile t in _layout.Tiles)
            if (t.OnMain == sel.OnMain && t.BranchIndex == sel.BranchIndex && t.DesktopIndex == sel.DesktopIndex)
                return t.Bounds;
        return null;
    }

    private BoardRow? BandOfBranch(int branchIndex)
    {
        if (_layout is null) return null;
        foreach (BoardRow r in _layout.Rows) if (!r.IsMain && r.BranchIndex == branchIndex) return r;
        return null;
    }

    private string? BranchName(int branchIndex)
        => branchIndex >= 0 && branchIndex < _base.Branches.Count ? _base.Branches[branchIndex].Name : null;

    private string LabelOf(DesktopSelection sel)
    {
        if (sel.OnMain)
            return sel.DesktopIndex >= 0 && sel.DesktopIndex < _base.TopRow.Count
                ? _base.TopRow[sel.DesktopIndex].Label : "desktop";
        if (sel.BranchIndex < 0 || sel.BranchIndex >= _base.Branches.Count) return "desktop";
        IReadOnlyList<NavMapTile> desks = _base.Branches[sel.BranchIndex].Desktops;
        return sel.DesktopIndex >= 0 && sel.DesktopIndex < desks.Count ? desks[sel.DesktopIndex].Label : "desktop";
    }

    // ── Render ───────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!_initialised) { InitSelection(); _initialised = true; }
        ClampSelection();
        _colOfRow[KeyOfRow(_row)] = _col; // this row's resume column, for a later step back into it

        double width = _stage.HostWidth > 0 ? _stage.HostWidth : 1280;
        double height = _stage.HostHeight > 0 ? _stage.HostHeight : 800;

        // The layout the render reports is what a drag hit-tests against, so it's refreshed with the board.
        // Both renderers fill it in the same scheme, so click/select and drag-rearrange work identically
        // whichever style is showing. Metro takes no delete callbacks (no × badges — Del still deletes) and
        // pulses the train only when the OS allows motion.
        // A theme switch changes the metrics; App reframes the shared camera when it flips the style.
        MapStyle style = _stage.MapStyle;

        var layout = new BoardLayout();
        NavMap display = BuildDisplayMap();
        IScenePainter painter = ScenePainters.For(style, WindowFx.SystemAnimationsEnabled());
        Control board = SceneRenderer.Render(painter, display, width, height, 1.0, _camera,
            onTopClick: SelectTop,
            onBranchClick: SelectBranch,
            onTopDelete: i => DeleteDesktopRequested?.Invoke(new DesktopSelection(true, -1, i)),
            onBranchDelete: (g, d) => DeleteDesktopRequested?.Invoke(new DesktopSelection(false, g, d)),
            onTopActivate: i => JumpTopRequested?.Invoke(i),
            onBranchActivate: (g, d) => JumpBranchRequested?.Invoke(g, d),
            layout: layout);
        _layout = layout;

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(BuildLegend());
        if (BuildHistory() is { } history) _root.Children.Add(history);
        _root.Children.Add(_dragLayer); // topmost: drag feedback draws over the board and the legend
        RenderDragLayer();

        // A switch or a closing prompt can surface a foreground window above the pinned host — re-lift so
        // the board stays visible (mirrors MoveContent.RenderTargeting).
        _stage.BringToFront();
    }

    // Start the cursor on the desktop the user is actually on, so the blue selection begins "here".
    private void InitSelection()
    {
        if (_base.OnTop) { _row = Split; _col = _base.TopCursor; return; }
        for (int gi = 0; gi < _base.Branches.Count; gi++)
        {
            var ds = _base.Branches[gi].Desktops;
            for (int j = 0; j < ds.Count; j++)
                if (ds[j].IsCurrent) { _row = gi < Split ? gi : gi + 1; _col = j; return; }
        }
        _row = Split; _col = 0;
    }

    private void ClampSelection()
    {
        _row = Math.Clamp(_row, 0, RowCount - 1);
        _col = Math.Clamp(_col, 0, Math.Max(0, TilesInRow(_row) - 1));
    }

    // Recolour the base map so the *selection* is the blue focus cell (IsCurrent) and the desktop you're
    // actually on keeps the green "here" marker (IsHere). The shared renderer reads those to place the blue
    // ring and the green train; the camera keeps the selection in view as it moves.
    //
    // The per-row cursor fields are also re-pointed at the selection's column, but that is now belt-and-braces:
    // the shared layout aligns every row at its first desktop (column 0), so a row's cursor no longer affects
    // where it's drawn. It's kept only so the display map stays internally consistent (and a good selection
    // fallback). See docs/design/scene-camera.md.
    private NavMap BuildDisplayMap()
    {
        bool selMain = RowIsMain(_row);
        int selBranch = selMain ? -1 : BranchOfRow(_row);

        var top = _base.TopRow
            .Select((t, i) => t with { IsCurrent = selMain && i == _col, IsHere = t.IsCurrent })
            .ToList();

        var branches = _base.Branches.Select((g, gi) =>
        {
            bool selHere = !selMain && gi == selBranch;
            var desks = g.Desktops
                .Select((d, j) => d with { IsCurrent = selHere && j == _col, IsHere = d.IsCurrent })
                .ToList();
            return g with
            {
                Desktops = desks,
                IsCurrentLevel = g.IsCurrentLevel || selHere, // keep the branch you're selecting into bright
                Cursor = selHere ? _col : ColumnIn(gi, g.Cursor, g.Desktops.Count),
            };
        }).ToList();

        return _base with
        {
            TopRow = top, Branches = branches,
            OnTop = selMain,
            TopCursor = selMain ? _col : ColumnIn(-1, _base.TopCursor, _base.TopRow.Count),
        };
    }

    // The column the selection resumes at when it steps into a row that doesn't currently hold it: where the
    // selection last sat in that row, else the model's own cursor. Clamped, since a remembered column can
    // outlive a row that has since lost desktops.
    private int ColumnIn(int key, int modelCursor, int count)
        => Math.Clamp(_colOfRow.TryGetValue(key, out int col) ? col : modelCursor, 0, Math.Max(0, count - 1));

    // ── Shortcut legend (top-left) ─────────────────────────────────────────────────

    private Control BuildLegend()
    {
        var rows = new StackPanel { Spacing = 7 };
        rows.Children.Add(new TextBlock
        {
            Text = "Manage desktops", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });
        rows.Children.Add(LegendRow("←→↑↓", "select a desktop"));
        rows.Children.Add(LegendRow("Enter", "switch to selected"));
        rows.Children.Add(LegendRow("Ctrl+Alt+←→↑↓", "switch to a desktop"));
        rows.Children.Add(LegendRow("Ctrl+←→↑↓", "move this desktop"));
        rows.Children.Add(LegendRow("Shift+↑↓", "move this row (branch or main)"));
        rows.Children.Add(LegendRow("r", "rename desktop"));
        rows.Children.Add(LegendRow("Shift+r", "rename branch"));
        rows.Children.Add(LegendRow("Del", "delete desktop"));
        rows.Children.Add(LegendRow("Shift+Del", "delete branch"));
        rows.Children.Add(LegendRow("n", "new desktop in row"));
        rows.Children.Add(LegendRow("b", "new branch"));
        rows.Children.Add(LegendRow("m", "move windows"));
        rows.Children.Add(LegendRow("Shift+m", "pull windows here"));
        rows.Children.Add(LegendRow("f", "find a desktop"));
        rows.Children.Add(LegendRow("p", "command palette"));
        rows.Children.Add(LegendRow("v", _stage.MapStyle switch
        {
            MapStyle.Board => "metro view",
            MapStyle.Metro => "ascii view",
            _ => "board view",
        }));
        rows.Children.Add(LegendRow("Tab", "spatial view"));
        rows.Children.Add(LegendRow("Esc", "close"));
        rows.Children.Add(new TextBlock
        {
            Text = "click to select · double-click to switch", FontSize = 11, Foreground = FgDim,
            Margin = new Thickness(0, 5, 0, 0),
        });
        rows.Children.Add(new TextBlock
        {
            Text = "drag a desktop or a branch to move it", FontSize = 11, Foreground = FgDim,
        });

        var legend = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 24, 0, 0), Child = rows,
        };
        // A row's band can run under the legend; swallow presses here so reading the legend never grabs the
        // branch behind it.
        legend.PointerPressed += (_, e) => e.Handled = true;
        return legend;
    }

    private static Control LegendRow(string key, string desc)
    {
        var cap = new Border
        {
            Background = new SolidColorBrush(KeyCapBg),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            },
        };
        Grid.SetColumn(cap, 0);
        var label = new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        grid.Children.Add(cap);
        grid.Children.Add(label);
        return grid;
    }

    // ── History queue (top-right) ──────────────────────────────────────────────────

    private const int HistoryShown = 8; // the trail can be long; the panel shows its newest end

    // The breadcrumb trail, newest at the bottom: where navigation has taken you, with the cursor's
    // entry accented (that's where undo/redo currently stands) and the redo tail — entries undone past —
    // dimmed. Purely informative: the history chords drive it (Ctrl+Alt + A/S/Q), the panel never
    // navigates. Null (panel hidden) until there's a trail to show.
    private Control? BuildHistory()
    {
        IReadOnlyList<HistoryCrumb> crumbs = HistoryProvider?.Invoke() ?? Array.Empty<HistoryCrumb>();
        if (crumbs.Count == 0) return null;

        var rows = new StackPanel { Spacing = 5 };
        rows.Children.Add(new TextBlock
        {
            Text = "History", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });

        int first = Math.Max(0, crumbs.Count - HistoryShown);
        if (first > 0)
            rows.Children.Add(new TextBlock { Text = $"… {first} earlier", FontSize = 10, Foreground = FgDim });
        for (int i = first; i < crumbs.Count; i++)
        {
            HistoryCrumb c = crumbs[i];
            var marker = new TextBlock
            {
                Text = c.IsCurrent ? "●" : "○", FontSize = 9,
                Foreground = c.IsCurrent ? Accent : FgDim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = c.Label, FontSize = 12,
                Foreground = c.IsCurrent ? Fg : FgDim,
                FontWeight = c.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                // The redo tail (entries undone past) reads as "ahead of you" — faded, not just dim.
                Opacity = c.IsAhead ? 0.55 : 1.0,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Opacity = c.IsAhead ? 0.7 : 1.0 };
            line.Children.Add(marker);
            line.Children.Add(label);
            rows.Children.Add(line);
        }

        rows.Children.Add(new TextBlock
        {
            Text = "Ctrl+Alt + A back · S forward · Q flip", FontSize = 10, Foreground = FgDim,
            Margin = new Thickness(0, 5, 0, 0),
        });

        var panel = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 24, 0), MaxWidth = 260, Child = rows,
        };
        // A row's band can run under the panel; swallow presses so reading it never grabs what's behind
        // (mirrors the legend).
        panel.PointerPressed += (_, e) => e.Handled = true;
        return panel;
    }
}

/// <summary>One entry of the top-right history panel: the desktop's display label, whether the undo/redo
/// cursor stands on it, and whether it sits ahead of the cursor (the redo tail).</summary>
internal readonly record struct HistoryCrumb(string Label, bool IsCurrent, bool IsAhead);

/// <summary>Which desktop the map has selected: a main-timeline desktop (<paramref name="OnMain"/> true,
/// <paramref name="DesktopIndex"/> = its top-row index) or a branch desktop (<paramref name="BranchIndex"/>
/// + <paramref name="DesktopIndex"/>).</summary>
internal readonly record struct DesktopSelection(bool OnMain, int BranchIndex, int DesktopIndex);
