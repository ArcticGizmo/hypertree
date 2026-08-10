using System;
using System.Collections.Generic;
using System.Linq;
using Hypertree.Desktops;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers direction-based spatial navigation: nearest room in a direction (favouring the travel axis), edges,
/// and the group lookup the "show before moving" reveal keys off. Pure — no Avalonia.
/// </summary>
public class SpatialNavigationTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static Guid G(int n) => new($"{n:D8}-aaaa-0000-0000-000000000000");

    private static SpatialRoom R(int id, int x, int y, Guid group)
        => new(D(id), $"d{id}", new GridPos(x, y), group, group == Guid.Empty, false, false, 1);

    private static SpatialScene Scene(params SpatialRoom[] rooms)
    {
        var groups = rooms.Select(r => r.GroupId).Distinct()
            .Select(g => new SpatialGroup(g, g.ToString(), "#F4795B", g == Guid.Empty,
                                          rooms.Where(r => r.GroupId == g).Select(r => r.Id).ToList()))
            .ToList();
        return new SpatialScene(groups, rooms);
    }

    [Fact]
    public void Right_picks_the_nearest_room_to_the_right()
    {
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 0, Guid.Empty), R(2, 5, 0, Guid.Empty));
        Assert.Equal(D(1), SpatialNavigation.NextInDirection(scene, D(0), 1, 0)); // nearest, not the far one
    }

    [Fact]
    public void Up_and_down_move_along_the_vertical_axis()
    {
        var scene = Scene(R(0, 0, 2, Guid.Empty), R(1, 0, 0, Guid.Empty), R(2, 0, 5, Guid.Empty));
        Assert.Equal(D(1), SpatialNavigation.NextInDirection(scene, D(0), 0, -1)); // up
        Assert.Equal(D(2), SpatialNavigation.NextInDirection(scene, D(0), 0, 1));  // down
    }

    [Fact]
    public void An_edge_returns_null()
    {
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 0, Guid.Empty));
        Assert.Null(SpatialNavigation.NextInDirection(scene, D(0), -1, 0)); // nothing to the left of the leftmost
    }

    [Fact]
    public void Prefers_the_travel_axis_over_a_closer_off_axis_room()
    {
        // A room one down-and-over vs a room straight right but slightly further: right should win on X travel.
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 3, Guid.Empty), R(2, 2, 0, Guid.Empty));
        Assert.Equal(D(2), SpatialNavigation.NextInDirection(scene, D(0), 1, 0));
    }

    [Fact]
    public void Prefers_an_aligned_room_over_a_closer_diagonal()
    {
        // A diagonal room sits closer, but one lines up exactly on the row: the aligned room wins even
        // though it's further, because a diagonal is only a fallback when the axis is empty.
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 1, Guid.Empty), R(2, 3, 0, Guid.Empty));
        Assert.Equal(D(2), SpatialNavigation.NextInDirection(scene, D(0), 1, 0));
    }

    [Fact]
    public void Falls_back_to_a_diagonal_when_nothing_is_aligned()
    {
        // Nothing sits on the row, so the nearest in-cone diagonal is chosen.
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 1, Guid.Empty), R(2, 3, 2, Guid.Empty));
        Assert.Equal(D(1), SpatialNavigation.NextInDirection(scene, D(0), 1, 0));
    }

    [Fact]
    public void GroupOf_reports_a_rooms_group_for_the_crossing_check()
    {
        var scene = Scene(R(0, 0, 0, Guid.Empty), R(1, 1, 0, G(1)));
        Assert.Equal(Guid.Empty, SpatialNavigation.GroupOf(scene, D(0))); // main
        Assert.Equal(G(1), SpatialNavigation.GroupOf(scene, D(1)));
        Assert.NotEqual(SpatialNavigation.GroupOf(scene, D(0)), SpatialNavigation.GroupOf(scene, D(1))); // a crossing
    }
}
