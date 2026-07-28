using Hypertree.Layout;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>Covers the world-space layout the shared renderer builds: row ordering, first-desktop alignment,
/// uniform vertical pitch, and where the selection lands. Pure geometry — no Avalonia, no renderer.</summary>
public class SceneLayoutTests
{
    private static readonly SceneMetrics M = new(CellStride: 100, CellWidth: 80, CellHeight: 60,
                                                 RowPitch: 120, RowHeight: 90);

    private static NavMapTile T(string label, bool current = false, bool here = false, int windows = 1)
        => new(label, current, here, windows);

    private static NavMapBranch B(int index, string name, bool active, int cursor, params NavMapTile[] desks)
        => new(index, name, desks, active, cursor);

    // main a b c (b current), with one branch above and one below main.
    private static NavMap SampleMap() => new(
        TopRow: new[] { T("a"), T("b", current: true), T("c") },
        TopCursor: 1,
        OnTop: true,
        Branches: new[]
        {
            B(0, "above", active: false, cursor: 0, T("x"), T("y")),
            B(1, "below", active: false, cursor: 0, T("p")),
        },
        TopPosition: 1); // one branch above main, the rest below

    [Fact]
    public void Rows_are_in_draw_order_branches_above_main_then_below()
    {
        Scene scene = Scene.From(SampleMap());
        Assert.Equal(3, scene.Rows.Count);
        Assert.Equal("above", scene.Rows[0].Name);
        Assert.True(scene.Rows[1].IsMain);
        Assert.Equal("below", scene.Rows[2].Name);
    }

    [Fact]
    public void Every_rows_first_desktop_shares_the_same_world_column()
    {
        var layout = new SceneLayout(Scene.From(SampleMap()), M);
        double col0 = layout.CellRect(0, 0).CenterX;
        for (int r = 0; r < layout.RowCount; r++)
            Assert.Equal(col0, layout.CellRect(r, 0).CenterX, precision: 6);
        Assert.Equal(0, col0, precision: 6); // column 0 anchors the world origin
    }

    [Fact]
    public void Cells_step_by_the_cell_stride_along_a_row()
    {
        var layout = new SceneLayout(Scene.From(SampleMap()), M);
        // Main row is index 1; its three desktops sit at 0, stride, 2*stride.
        Assert.Equal(0, layout.CellRect(1, 0).CenterX, precision: 6);
        Assert.Equal(M.CellStride, layout.CellRect(1, 1).CenterX, precision: 6);
        Assert.Equal(2 * M.CellStride, layout.CellRect(1, 2).CenterX, precision: 6);
    }

    [Fact]
    public void Rows_stack_on_a_uniform_pitch()
    {
        var layout = new SceneLayout(Scene.From(SampleMap()), M);
        double y0 = layout.RowRect(0).CenterY;
        double y1 = layout.RowRect(1).CenterY;
        double y2 = layout.RowRect(2).CenterY;
        Assert.Equal(M.RowPitch, y1 - y0, precision: 6);
        Assert.Equal(M.RowPitch, y2 - y1, precision: 6);
    }

    [Fact]
    public void Selection_rect_is_the_current_cell()
    {
        Scene scene = Scene.From(SampleMap());
        // "b" is current: main row (index 1), column 1.
        Assert.Equal(1, scene.SelectionRow);
        Assert.Equal(1, scene.SelectionCol);

        var layout = new SceneLayout(scene, M);
        Assert.Equal(layout.CellRect(1, 1), layout.SelectionRect);
    }

    [Fact]
    public void Selection_follows_into_a_branch_when_that_is_the_current_level()
    {
        // Inside the "below" branch: not OnTop, the branch desktop is current.
        NavMap map = new(
            TopRow: new[] { T("a"), T("b"), T("c") },
            TopCursor: 1,
            OnTop: false,
            Branches: new[] { B(0, "below", active: true, cursor: 1, T("p"), T("q", current: true)) },
            TopPosition: 0); // branch renders below main

        Scene scene = Scene.From(map);
        // Rows: [main, below]; selection is branch row 1, column 1.
        Assert.Equal(1, scene.SelectionRow);
        Assert.Equal(1, scene.SelectionCol);
        Assert.False(scene.Rows[0].Cells.Any(c => c.Selected)); // main carries no selection while dived
    }
}
