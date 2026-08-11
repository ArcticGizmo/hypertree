using System.Collections.Generic;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers where a new desktop lands: the closest empty cell to its anchor, with equal-distance ties resolved
/// by the fixed compass priority right → bottom → left → top, then the four diagonals.
/// </summary>
public class SpatialPlacementTests
{
    private static GridPos Nearest(GridPos anchor, params GridPos[] occupied)
        => SpatialPlacement.NearestEmpty(anchor, new HashSet<GridPos>(occupied));

    [Fact]
    public void A_free_anchor_is_returned_unchanged()
        => Assert.Equal(new GridPos(3, 4), Nearest(new GridPos(3, 4)));

    [Fact]
    public void Occupied_anchor_prefers_the_cell_to_the_right()
        => Assert.Equal(new GridPos(1, 0), Nearest(new GridPos(0, 0), new GridPos(0, 0)));

    [Fact]
    public void Right_blocked_falls_to_bottom_then_left_then_top()
    {
        var anchor = new GridPos(0, 0);
        Assert.Equal(new GridPos(0, 1), Nearest(anchor, anchor, new GridPos(1, 0)));
        Assert.Equal(new GridPos(-1, 0), Nearest(anchor, anchor, new GridPos(1, 0), new GridPos(0, 1)));
        Assert.Equal(new GridPos(0, -1),
            Nearest(anchor, anchor, new GridPos(1, 0), new GridPos(0, 1), new GridPos(-1, 0)));
    }

    [Fact]
    public void Orthogonals_full_falls_to_the_bottom_right_diagonal()
    {
        var anchor = new GridPos(0, 0);
        // All four orthogonal neighbours taken; the nearest remaining are the diagonals, BR first.
        var moved = Nearest(anchor, anchor,
            new GridPos(1, 0), new GridPos(0, 1), new GridPos(-1, 0), new GridPos(0, -1));
        Assert.Equal(new GridPos(1, 1), moved);
    }

    [Fact]
    public void Closer_cell_beats_a_higher_priority_but_farther_one()
    {
        // Right neighbour (distance 1) wins over the far right cells even though "right" is top priority:
        // distance is the primary key, priority only breaks ties.
        var anchor = new GridPos(0, 0);
        var moved = Nearest(anchor, anchor, new GridPos(2, 0)); // a gap at (1,0) exists
        Assert.Equal(new GridPos(1, 0), moved);
    }

    [Fact]
    public void Expands_outward_when_the_whole_first_ring_is_full()
    {
        var anchor = new GridPos(0, 0);
        var occupied = new List<GridPos>();
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                occupied.Add(new GridPos(dx, dy)); // anchor + all 8 neighbours
        var moved = SpatialPlacement.NearestEmpty(anchor, occupied);
        // Nearest free cells are the orthogonals at distance 2; right wins.
        Assert.Equal(new GridPos(2, 0), moved);
    }

    // ── Runs (a whole branch, laid out horizontally) ─────────────────────────────

    private static GridPos Run(GridPos anchor, int count, params GridPos[] occupied)
        => SpatialPlacement.NearestEmptyRun(anchor, count, new HashSet<GridPos>(occupied));

    [Fact]
    public void A_single_cell_run_matches_NearestEmpty()
    {
        var anchor = new GridPos(0, 0);
        Assert.Equal(Nearest(anchor, anchor), Run(anchor, 1, anchor));
    }

    [Fact]
    public void A_run_starts_to_the_right_of_an_occupied_anchor()
    {
        // Anchor taken (it's the selected room), right side clear: the run begins at (1,0) and extends right.
        Assert.Equal(new GridPos(1, 0), Run(new GridPos(0, 0), 3, new GridPos(0, 0)));
    }

    [Fact]
    public void A_run_skips_a_start_whose_body_would_overlap()
    {
        // Start-right at (1,0) is free but (2,0) is taken, so a width-2 run can't sit there; the next-nearest
        // clear pair of cells is the bottom row, beginning at (0,1).
        var moved = Run(new GridPos(0, 0), 2, new GridPos(0, 0), new GridPos(2, 0));
        Assert.Equal(new GridPos(0, 1), moved);
    }

    [Fact]
    public void A_free_anchor_seats_the_run_from_the_anchor()
        => Assert.Equal(new GridPos(3, 4), Run(new GridPos(3, 4), 3));
}
