using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive map — and the app's single "manage desktops" surface — presented on the shared
/// <see cref="OverlayStage"/>. Renders the full board on the primary monitor over the stage's dim
/// backdrop and drives a <b>selection cursor</b> that moves purely visually: the arrow keys (or a single
/// click) point at any desktop <b>without switching to it</b>, so you can inspect and manage the whole
/// layout from wherever you are. The selected tile carries the blue focus outline; the desktop you're
/// actually on keeps the green "here" marker (so the two never blur). Ctrl+Alt+Arrow still switches
/// desktops (handled by <c>App</c>) and re-homes the selection onto the desktop you land on; a
/// double-click does the same for a specific tile.
///
/// A shortcut legend in the top-left lists the management actions, each raised as an event for <c>App</c>
/// (which owns the <see cref="NavigationModel"/> and desktop controller): <b>r</b> rename, <b>Del</b>
/// delete desktop, <b>Shift+Del</b> delete branch, <b>n</b> new desktop, <b>b</b> new branch. Because it
/// lives on the persistent stage it survives the desktop switches of navigation (the stage is pinned to
/// every desktop). Closes on Esc, a backdrop click on another monitor, or toggling it off.
/// </summary>
internal sealed class MapOverlay : IStageContent
{
    private readonly OverlayStage _stage;

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly Color BtnBg = Color.Parse("#2A3444"), BtnBgHover = Color.Parse("#37455B"), BtnBorder = Color.Parse("#3C4A5E");
    private static readonly Color LegendBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22);
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A);

    private readonly Grid _root = new();
    private NavMap _base = new(Array.Empty<NavMapTile>(), 0, true, Array.Empty<NavMapBranch>());
    private bool _initialised;
    private int _row; // index into the combined row sequence (branches split around main)
    private int _col; // index within the current row

    /// <summary>Double-click / activate a desktop — jump there. Top-row index, or branch index + desktop.</summary>
    public event Action<int>? JumpTopRequested;
    public event Action<int, int>? JumpBranchRequested;
    /// <summary>Delete a desktop — the selected one (Del) or a clicked × badge.</summary>
    public event Action<DesktopSelection>? DeleteDesktopRequested;
    /// <summary>Delete an entire branch (Shift+Del) by its index.</summary>
    public event Action<int>? DeleteBranchRequested;
    /// <summary>Rename the selected desktop (r).</summary>
    public event Action<DesktopSelection>? RenameRequested;
    /// <summary>Create a new desktop (n) / a new branch (b).</summary>
    public event Action? NewDesktopRequested;
    public event Action? NewBranchRequested;
    /// <summary>Ctrl+F — open the finder (jump/create spotlight) from the map.</summary>
    public event Action? FinderRequested;
    /// <summary>The cog icon — open settings.</summary>
    public event Action? SettingsRequested;

    public MapOverlay(OverlayStage stage) => _stage = stage;

    public bool IsOpen => _stage.Current == this;

    /// <summary>The desktop the map currently has selected (for App: e.g. where a new branch should attach).</summary>
    public DesktopSelection Selection => CurrentSelection();

    /// <summary>Open the map, homing the selection onto the desktop you're currently on. A fresh root —
    /// the map is the durable base other surfaces open over and return to.</summary>
    public void Open(NavMap map)
    {
        _base = map;
        _initialised = false;
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
        else { _row = sel.BranchIndex < Split ? sel.BranchIndex : sel.BranchIndex + 1; _col = sel.DesktopIndex; }
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
    public void OnRemoved() => _initialised = false;

    public void OnKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Enter: JumpToSelection(); e.Handled = true; break;
            case Key.Left: Move(0, -1); e.Handled = true; break;
            case Key.Right: Move(0, +1); e.Handled = true; break;
            case Key.Up: Move(-1, 0); e.Handled = true; break;
            case Key.Down: Move(+1, 0); e.Handled = true; break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control): FinderRequested?.Invoke(); e.Handled = true; break;
            case Key.R: RenameRequested?.Invoke(CurrentSelection()); e.Handled = true; break;
            case Key.N: NewDesktopRequested?.Invoke(); e.Handled = true; break;
            case Key.B: NewBranchRequested?.Invoke(); e.Handled = true; break;
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
    private int TilesInRow(int row)
        => RowIsMain(row) ? _base.TopRow.Count : _base.Branches[BranchOfRow(row)].Desktops.Count;

    private void Move(int dRow, int dCol)
    {
        if (dRow != 0) _row = Math.Clamp(_row + dRow, 0, RowCount - 1);
        _col = Math.Clamp(_col + dCol, 0, Math.Max(0, TilesInRow(_row) - 1));
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
    // to App). onTop/onBranch mirror BoardView's tile callbacks.
    private void SelectTop(int index) { _row = Split; _col = index; Render(); }
    private void SelectBranch(int branchIndex, int desktopIndex)
    {
        _row = branchIndex < Split ? branchIndex : branchIndex + 1;
        _col = desktopIndex;
        Render();
    }

    // ── Render ───────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!_initialised) { InitSelection(); _initialised = true; }
        ClampSelection();

        double width = _stage.HostWidth > 0 ? _stage.HostWidth : 1280;
        double height = _stage.HostHeight > 0 ? _stage.HostHeight : 800;

        Control board = BoardView.Render(BuildDisplayMap(), width, height, 1.0,
            onTopClick: SelectTop,
            onBranchClick: SelectBranch,
            onTopDelete: i => DeleteDesktopRequested?.Invoke(new DesktopSelection(true, -1, i)),
            onBranchDelete: (g, d) => DeleteDesktopRequested?.Invoke(new DesktopSelection(false, g, d)),
            onTopActivate: i => JumpTopRequested?.Invoke(i),
            onBranchActivate: (g, d) => JumpBranchRequested?.Invoke(g, d));

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(BuildLegend());
        _root.Children.Add(BuildCog());

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

    // Recolour the base map so the *selection* is the blue focus tile and the desktop you're actually on
    // keeps the green "here" marker. BoardView centres on the IsCurrent row, so the selection stays
    // centred as it moves. (Mirrors the former RenameContent.)
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
                Cursor = selHere ? _col : g.Cursor,           // centre the selected branch on its cursor
            };
        }).ToList();

        return _base with
        {
            TopRow = top, Branches = branches,
            OnTop = selMain, TopCursor = selMain ? _col : _base.TopCursor,
        };
    }

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
        rows.Children.Add(LegendRow("r", "rename desktop"));
        rows.Children.Add(LegendRow("Del", "delete desktop"));
        rows.Children.Add(LegendRow("Shift+Del", "delete branch"));
        rows.Children.Add(LegendRow("n", "new desktop"));
        rows.Children.Add(LegendRow("b", "new branch"));
        rows.Children.Add(LegendRow("Ctrl+F", "find a desktop"));
        rows.Children.Add(LegendRow("Esc", "close"));
        rows.Children.Add(new TextBlock
        {
            Text = "click to select · double-click to switch", FontSize = 11, Foreground = FgDim,
            Margin = new Thickness(0, 5, 0, 0),
        });

        return new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 24, 0, 0), Child = rows,
        };
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

    private Control BuildCog()
    {
        var cog = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(BtnBg), BorderBrush = new SolidColorBrush(BtnBorder),
            BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 24, 0),
            Child = new TextBlock
            {
                Text = "⚙", FontSize = 17, Foreground = Fg,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        cog.PointerEntered += (_, _) => cog.Background = new SolidColorBrush(BtnBgHover);
        cog.PointerExited += (_, _) => cog.Background = new SolidColorBrush(BtnBg);
        cog.PointerPressed += (_, e) => { e.Handled = true; SettingsRequested?.Invoke(); };
        return cog;
    }
}

/// <summary>Which desktop the map has selected: a main-timeline desktop (<paramref name="OnMain"/> true,
/// <paramref name="DesktopIndex"/> = its top-row index) or a branch desktop (<paramref name="BranchIndex"/>
/// + <paramref name="DesktopIndex"/>).</summary>
internal readonly record struct DesktopSelection(bool OnMain, int BranchIndex, int DesktopIndex);
