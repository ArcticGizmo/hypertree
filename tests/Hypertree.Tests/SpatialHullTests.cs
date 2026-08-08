using System.Linq;
using Hypertree.Layout;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the "tetris" group outline: cells sharing an edge merge into one ring with the shared edge gone,
/// cells touching only at a corner stay separate shapes, and the inset holds the ring off the cell edges.
/// Pure geometry — no Avalonia, no painter.
/// </summary>
public class SpatialHullTests
{
    private const double Stride = 100, Inset = 10;

    private static GridPos[] Cells(params (int x, int y)[] c) => c.Select(p => new GridPos(p.x, p.y)).ToArray();

    private static IReadOnlyList<HullShape> Shapes(params (int x, int y)[] c)
        => SpatialHull.Shapes(Cells(c), Stride, Stride, Inset, Inset);

    [Fact]
    public void A_single_cell_is_one_inset_rectangle()
    {
        HullShape shape = Assert.Single(Shapes((0, 0)));
        IReadOnlyList<LayoutPoint> ring = Assert.Single(shape.Loops);
        Assert.Equal(4, ring.Count); // a rectangle

        // Cell spans world -50..50 (centre 0, stride 100); inset 10 pulls each edge in to -40..40.
        Assert.Equal(-40, ring.Min(p => p.X), 3);
        Assert.Equal(40, ring.Max(p => p.X), 3);
        Assert.Equal(-40, ring.Min(p => p.Y), 3);
        Assert.Equal(40, ring.Max(p => p.Y), 3);
    }

    [Fact]
    public void Edge_adjacent_cells_merge_into_one_ring_with_the_shared_edge_gone()
    {
        HullShape shape = Assert.Single(Shapes((0, 0), (1, 0))); // one merged shape, not two
        IReadOnlyList<LayoutPoint> ring = Assert.Single(shape.Loops);
        Assert.Equal(4, ring.Count); // still a rectangle — the shared inner edge dissolved, no 8-corner dumbbell

        // Two cells wide: world -50..150, inset to -40..140.
        Assert.Equal(-40, ring.Min(p => p.X), 3);
        Assert.Equal(140, ring.Max(p => p.X), 3);
    }

    [Fact]
    public void Vertically_adjacent_cells_also_merge()
    {
        HullShape shape = Assert.Single(Shapes((0, 0), (0, 1)));
        IReadOnlyList<LayoutPoint> ring = Assert.Single(shape.Loops);
        Assert.Equal(4, ring.Count);
        Assert.Equal(-40, ring.Min(p => p.Y), 3);
        Assert.Equal(140, ring.Max(p => p.Y), 3);
    }

    [Fact]
    public void Corner_touching_cells_stay_two_shapes()
    {
        // A diagonal pair shares no edge, so it is not one tetris piece — two separate hulls.
        Assert.Equal(2, Shapes((0, 0), (1, 1)).Count);
    }

    [Fact]
    public void An_L_shape_is_one_ring_of_six_corners()
    {
        // Three cells in an L: one edge-connected shape whose outline turns six times.
        HullShape shape = Assert.Single(Shapes((0, 0), (1, 0), (0, 1)));
        IReadOnlyList<LayoutPoint> ring = Assert.Single(shape.Loops);
        Assert.Equal(6, ring.Count);
    }

    [Fact]
    public void A_ring_of_cells_around_a_hole_yields_an_outer_ring_and_a_hole()
    {
        // Eight cells around an empty centre (1,1): one shape, two loops (outer boundary + the hole).
        var donut = Shapes((0, 0), (1, 0), (2, 0), (0, 1), (2, 1), (0, 2), (1, 2), (2, 2));
        HullShape shape = Assert.Single(donut);
        Assert.Equal(2, shape.Loops.Count);
        Assert.All(shape.Loops, loop => Assert.Equal(4, loop.Count)); // both rings are rectangles here
    }

    [Fact]
    public void A_separated_clump_is_a_second_shape()
    {
        // Two cells here, two far away: two shapes.
        Assert.Equal(2, Shapes((0, 0), (1, 0), (9, 9), (9, 8)).Count);
    }
}
