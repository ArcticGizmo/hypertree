using System;
using System.Collections.Generic;
using System.Linq;
using Hypertree.Desktops;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers Tidy: it reunites a group's scattered fragments into one contiguous block, moving each fragment
/// as a rigid unit (shapes preserved), and packs groups so none overlap. Pure geometry.
/// </summary>
public class SpatialTidyTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static Guid Grp(int n) => new($"{n:D8}-aaaa-0000-0000-000000000000");

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

    private static int Fragments(IEnumerable<GridPos> ps) => SpatialClusters.Fragments(ps.ToList()).Count;

    [Fact]
    public void Group_tidy_reunites_fragments_into_one_contiguous_block()
    {
        Guid g = Grp(1);
        // A pair at the origin and a pair flung far away — two fragments.
        var scene = Scene(R(0, 0, 0, g), R(1, 1, 0, g), R(2, 20, 20, g), R(3, 21, 20, g));
        Assert.Equal(2, Fragments(scene.Rooms.Select(r => r.Pos)));

        var moved = SpatialTidy.Group(scene, g);
        Assert.Equal(1, Fragments(moved.Values)); // now a single contiguous block
    }

    [Fact]
    public void Group_tidy_preserves_each_fragments_internal_shape()
    {
        Guid g = Grp(1);
        var scene = Scene(R(0, 0, 0, g), R(1, 1, 0, g), R(2, 20, 20, g), R(3, 21, 20, g));

        var moved = SpatialTidy.Group(scene, g);
        // The flung pair (2,3) was horizontally adjacent; after tidy it must still be, just translated.
        GridPos p2 = moved[D(2)], p3 = moved[D(3)];
        Assert.Equal(1, Math.Abs(p2.X - p3.X));
        Assert.Equal(0, p2.Y - p3.Y);
    }

    [Fact]
    public void Group_tidy_anchors_the_largest_fragment_in_place()
    {
        Guid g = Grp(1);
        // Fragment A is 3 rooms (the largest), fragment B is 1 stray.
        var scene = Scene(R(0, 0, 0, g), R(1, 1, 0, g), R(2, 2, 0, g), R(9, 30, 30, g));
        var moved = SpatialTidy.Group(scene, g);

        Assert.Equal(new GridPos(0, 0), moved[D(0)]); // the big fragment didn't move
        Assert.Equal(new GridPos(1, 0), moved[D(1)]);
        Assert.Equal(new GridPos(2, 0), moved[D(2)]);
        Assert.Equal(1, Fragments(moved.Values));      // the stray joined it
    }

    [Fact]
    public void Tidy_all_packs_groups_without_overlap()
    {
        Guid a = Grp(1), b = Grp(2);
        // Two groups, each scattered, and currently overlapping each other's cells.
        var scene = Scene(
            R(0, 0, 0, a), R(1, 1, 0, a), R(2, 40, 40, a),
            R(10, 0, 0, b), R(11, 1, 1, b), R(12, 40, 41, b));

        var moved = SpatialTidy.All(scene);

        Assert.Equal(6, moved.Count);
        // No two rooms share a cell.
        Assert.Equal(6, moved.Values.Distinct().Count());
        // Each group is contiguous afterwards.
        Assert.Equal(1, Fragments(new[] { moved[D(0)], moved[D(1)], moved[D(2)] }));
        Assert.Equal(1, Fragments(new[] { moved[D(10)], moved[D(11)], moved[D(12)] }));
    }
}
