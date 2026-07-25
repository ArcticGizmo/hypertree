using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The "rename a desktop" surface, hosted on the shared <see cref="OverlayStage"/>. Renders the full
/// board like the map, but drives a <b>selection cursor</b> with the arrow keys that moves purely
/// visually — it never switches desktops, so you can point at any desktop without leaving the one you're
/// on. The selected tile carries the blue focus outline; the desktop you're actually on keeps the green
/// "here" marker (so the two never blur together). Enter raises <see cref="RenameRequested"/> for App to
/// open the rename prompt; the surface stays up afterwards so several desktops can be renamed in one
/// session. Esc closes it.
///
/// Holds no model: the board is pulled via <see cref="BoardProvider"/> and the rename itself is App's job
/// (it owns the <see cref="NavigationModel"/> and desktop controller). Mirrors <see cref="MoveContent"/>.
/// </summary>
internal sealed class RenameContent : IStageContent
{
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly Color BarBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22); // instruction pill background

    private readonly Grid _root = new();
    private OverlayStage? _stage;
    private NavMap _base = new(Array.Empty<NavMapTile>(), 0, true, Array.Empty<NavMapBranch>());
    private bool _initialised;
    private int _row;   // index into the combined row sequence (branches split around main)
    private int _col;   // index within the current row

    /// <summary>Supplies the board to render/select over (App: the live map).</summary>
    public Func<NavMap>? BoardProvider;
    /// <summary>Enter on a selected desktop — App opens the rename prompt for it.</summary>
    public event Action<DesktopSelection>? RenameRequested;

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public bool Dim => true;
    public bool DismissOnDeactivate => false; // survive the deactivation the rename prompt (or a switch) causes
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage) { _stage = stage; Render(); }
    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: _stage?.Dismiss(); e.Handled = true; break;
            case Key.Left: Move(0, -1); e.Handled = true; break;
            case Key.Right: Move(0, +1); e.Handled = true; break;
            case Key.Up: Move(-1, 0); e.Handled = true; break;
            case Key.Down: Move(+1, 0); e.Handled = true; break;
            case Key.Enter: RenameRequested?.Invoke(CurrentSelection()); e.Handled = true; break;
        }
    }

    /// <summary>Re-pull the board (labels change after a rename) and redraw, keeping the selection where it
    /// was. Called by App after each rename so the surface stays open for the next one.</summary>
    public void Refresh() => Render();

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

    // ── Render ───────────────────────────────────────────────────────────────────

    private void Render()
    {
        _base = BoardProvider?.Invoke() ?? _base;
        if (!_initialised) { InitSelection(); _initialised = true; }
        ClampSelection();

        double width = _stage?.HostWidth ?? 1280, height = _stage?.HostHeight ?? 800;
        Control board = BoardView.Render(BuildDisplayMap(), width, height, 1.0);

        Border banner = HintBar("Select a desktop to rename · ←→↑↓ move · Enter to rename · Esc to close");

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(banner);

        // A switch or a closing prompt can surface a foreground window above the pinned host — re-lift so
        // the board stays visible (mirrors MoveContent.RenderTargeting).
        _stage?.BringToFront();
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
    // centred as it moves.
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

    private static Border HintBar(string text) => new()
    {
        Background = new SolidColorBrush(BarBg),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 9),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 24, 0, 0),
        Child = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };
}

/// <summary>Which desktop the rename surface has selected: a main-timeline desktop (<paramref name="OnMain"/>
/// true, <paramref name="DesktopIndex"/> = its top-row index) or a branch desktop
/// (<paramref name="BranchIndex"/> + <paramref name="DesktopIndex"/>).</summary>
internal readonly record struct DesktopSelection(bool OnMain, int BranchIndex, int DesktopIndex);
