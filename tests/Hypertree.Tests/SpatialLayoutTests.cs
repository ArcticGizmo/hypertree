using System;
using System.Linq;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the spatial world layout and fragment detection: rooms placed on the grid pitch, the selection
/// rect the camera follows, world spans, and how a group splits into hulls when its rooms drift apart. Pure
/// geometry — no Avalonia, no painter.
/// </summary>
public class SpatialLayoutTests
{
    private static readonly SceneMetrics M = new(CellStride: 100, CellWidth: 80, CellHeight: 60,
                                                 RowPitch: 90, RowHeight: 60);

    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static SpatialRoom R(int id, int x, int y, Guid group, bool sel = false, bool here = false)
        => new(D(id), $"d{id}", new GridPos(x, y), group, group == Guid.Empty, sel, here, 1);

    // ── SpatialClusters ───────────────────────────────────────────────────────────

    [Fact]
    public void Touching_cells_including_diagonals_form_one_fragment()
    {
        var pos = new[] { new GridPos(0, 0), new GridPos(1, 0), new GridPos(1, 1) }; // an L, all touching
        Assert.Single(SpatialClusters.Fragments(pos));
    }

    [Fact]
    public void A_gap_splits_a_group_into_two_fragments()
    {
        var pos = new[] { new GridPos(0, 0), new GridPos(1, 0), new GridPos(9, 9), new GridPos(9, 8) };
        var frags = SpatialClusters.Fragments(pos);
        Assert.Equal(2, frags.Count);
        Assert.Equal(new[] { 0, 1 }, frags[0]);
        Assert.Equal(new[] { 2, 3 }, frags[1]);
    }

    [Fact]
    public void Empty_input_has_no_fragments()
        => Assert.Empty(SpatialClusters.Fragments(Array.Empty<GridPos>()));

    // ── SpatialLayout ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rooms_sit_at_the_grid_pitch()
    {
        var scene = new SpatialScene(
            new[] { new SpatialGroup(Guid.Empty, "main", SpatialPalette.Main, true, new[] { D(0), D(1) }) },
            new[] { R(0, 0, 0, Guid.Empty), R(1, 2, 1, Guid.Empty) });
        var layout = new SpatialLayout(scene, M);

        Assert.Equal(0, layout.RoomRect(D(0)).CenterX, precision: 6);
        Assert.Equal(0, layout.RoomRect(D(0)).CenterY, precision: 6);
        Assert.Equal(2 * M.CellStride, layout.RoomRect(D(1)).CenterX, precision: 6);
        Assert.Equal(1 * M.RowPitch, layout.RoomRect(D(1)).CenterY, precision: 6);
    }

    [Fact]
    public void Selection_rect_is_the_selected_room()
    {
        var scene = new SpatialScene(
            new[] { new SpatialGroup(Guid.Empty, "main", SpatialPalette.Main, true, new[] { D(0), D(1) }) },
            new[] { R(0, 0, 0, Guid.Empty), R(1, 3, 2, Guid.Empty, sel: true) });
        var layout = new SpatialLayout(scene, M);
        Assert.Equal(layout.RoomRect(D(1)), layout.SelectionRect);
    }

    [Fact]
    public void World_span_covers_every_room()
    {
        var scene = new SpatialScene(
            new[] { new SpatialGroup(Guid.Empty, "main", SpatialPalette.Main, true, new[] { D(0), D(1) }) },
            new[] { R(0, -1, 0, Guid.Empty), R(1, 4, 3, Guid.Empty) });
        var layout = new SpatialLayout(scene, M);

        (double xLo, double xHi) = layout.WorldX();
        (double yLo, double yHi) = layout.WorldY();
        Assert.Equal(-1 * M.CellStride - M.CellWidth / 2, xLo, precision: 6);
        Assert.Equal(4 * M.CellStride + M.CellWidth / 2, xHi, precision: 6);
        Assert.Equal(0 - M.CellHeight / 2, yLo, precision: 6);
        Assert.Equal(3 * M.RowPitch + M.CellHeight / 2, yHi, precision: 6);
    }

    [Fact]
    public void A_scattered_group_yields_one_hull_per_fragment_with_a_single_primary()
    {
        Guid g = new("00000001-aaaa-0000-0000-000000000000");
        var scene = new SpatialScene(
            new[] { new SpatialGroup(g, "feat", "#F4795B", false, new[] { D(0), D(1), D(2) }) },
            new[] { R(0, 0, 0, g), R(1, 1, 0, g), R(2, 9, 9, g) }); // a pair + a lone stray
        var layout = new SpatialLayout(scene, M);

        var hulls = layout.Hulls(10, 10);
        Assert.Equal(2, hulls.Count);
        Assert.Single(hulls, h => h.Primary);                       // exactly one badge anchor
        Assert.True(hulls.First(h => h.Primary).Rect.Top <= hulls.Last().Rect.Top); // the top-most fragment
    }

    [Fact]
    public void A_contiguous_group_yields_a_single_hull()
    {
        Guid g = new("00000002-aaaa-0000-0000-000000000000");
        var scene = new SpatialScene(
            new[] { new SpatialGroup(g, "rel", "#5BC8F4", false, new[] { D(0), D(1), D(2), D(3) }) },
            new[] { R(0, 5, 0, g), R(1, 6, 0, g), R(2, 5, 1, g), R(3, 6, 1, g) }); // a 2×2 block
        var layout = new SpatialLayout(scene, M);
        Assert.Single(layout.Hulls(10, 10));
    }
}
