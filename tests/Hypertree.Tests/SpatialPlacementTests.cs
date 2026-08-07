using System;
using System.Collections.Generic;
using System.Linq;
using Hypertree.Desktops;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers overlap resolution: a moved room wins its cell and the room it lands on is bumped to the nearest
/// free cell, without creating new overlaps. Pure grid maths.
/// </summary>
public class SpatialPlacementTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static (DesktopId, GridPos) At(int id, int x, int y) => (D(id), new GridPos(x, y));

    [Fact]
    public void The_mover_keeps_its_cell_and_the_occupant_is_bumped()
    {
        // Room 0 was moved onto (5,5) where room 1 already sits.
        var rooms = new List<(DesktopId, GridPos)> { At(0, 5, 5), At(1, 5, 5) };
        var fixes = SpatialPlacement.ResolveOverlaps(rooms, new HashSet<DesktopId> { D(0) });

        Assert.False(fixes.ContainsKey(D(0)));                 // the mover never yields
        Assert.True(fixes.ContainsKey(D(1)));                  // the occupant moved
        Assert.NotEqual(new GridPos(5, 5), fixes[D(1)]);       // …off the shared cell
        Assert.Equal(1, Chebyshev(new GridPos(5, 5), fixes[D(1)])); // to an adjacent (nearest) cell
    }

    [Fact]
    public void A_clear_move_relocates_nobody()
    {
        var rooms = new List<(DesktopId, GridPos)> { At(0, 0, 0), At(1, 3, 3) };
        Assert.Empty(SpatialPlacement.ResolveOverlaps(rooms, new HashSet<DesktopId> { D(0) }));
    }

    [Fact]
    public void Cascading_bumps_never_leave_two_rooms_on_a_cell()
    {
        // A moved room lands on a tight cluster; the shove must cascade to distinct cells.
        var rooms = new List<(DesktopId, GridPos)>
        {
            At(0, 1, 1), // the mover
            At(1, 1, 1), At(2, 2, 1), At(3, 1, 2), At(4, 2, 2), // a packed block around it
        };
        var fixes = SpatialPlacement.ResolveOverlaps(rooms, new HashSet<DesktopId> { D(0) });

        // Final positions = the mover's fixed cell + each settled room's (possibly relocated) cell.
        var final = new List<GridPos> { new(1, 1) };
        foreach ((DesktopId id, GridPos _) in rooms.Skip(1))
            final.Add(fixes.TryGetValue(id, out GridPos p) ? p : rooms.First(r => r.Item1 == id).Item2);

        Assert.Equal(final.Count, final.Distinct().Count()); // every room on its own cell
    }

    private static int Chebyshev(GridPos a, GridPos b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
