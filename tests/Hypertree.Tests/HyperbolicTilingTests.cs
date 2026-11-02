using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Hypertree.Desktops;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the hyperbolic map: Möbius isometries compose and invert, the {4,5} tiling really has five squares
/// round every corner and neighbouring rooms share an edge, the walk-from-the-anchor layout never stacks two
/// rooms on one tile, arrows turn with the spin, and the glide starts where the eye was and lands on the new
/// layout. Pure — no Avalonia.
/// </summary>
public class HyperbolicTilingTests
{
    private static void Near(Complex expected, Complex actual, double tol = 1e-9) =>
        Assert.True((expected - actual).Magnitude < tol, $"expected {expected}, got {actual}");

    // ── Möbius ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_translation_carries_the_centre_where_asked_and_inverts_back()
    {
        var p = new Complex(0.3, -0.4);
        Mobius m = Mobius.CentreTo(p).Then(Mobius.Rotate(0.7));
        Near(p, m.Apply(Complex.Zero));
        Near(new Complex(0.1, 0.2), m.Inverse.Apply(m.Apply(new Complex(0.1, 0.2))));
        Near(p, m.Centre);
        Assert.Equal(0.7, m.Twist, precision: 9);
    }

    [Fact]
    public void A_positive_rotation_turns_right_into_down_on_screen()
    {
        Near(Complex.ImaginaryOne, Mobius.Rotate(Math.PI / 2).Apply(Complex.One));
    }

    [Fact]
    public void Lerp_hits_both_ends_and_glides_the_centre_along_the_geodesic()
    {
        Mobius a = Mobius.CentreTo(new Complex(-0.5, 0)), b = Mobius.CentreTo(new Complex(0.5, 0)).Then(Mobius.Rotate(1));
        Near(a.Centre, Mobius.Lerp(a, b, 0).Centre);
        Near(b.Centre, Mobius.Lerp(a, b, 1).Centre);
        Assert.Equal(1, Mobius.Lerp(a, b, 1).Twist, precision: 9);
        // Symmetric endpoints on a diameter: halfway is the centre of the disk.
        Near(Complex.Zero, Mobius.Lerp(a, b, 0.5).Centre);
    }

    // ── Tiling ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_step_right_carries_the_left_edge_onto_the_right_edge()
    {
        double mid = HyperbolicTiling.KleinToDisk(new Complex(HyperbolicTiling.KleinHalfSide, 0)).Real;
        Near(new Complex(mid, 0), HyperbolicTiling.StepRight.Apply(new Complex(-mid, 0)));
        Near(new Complex(0, mid), HyperbolicTiling.StepDown.Apply(new Complex(0, -mid)));
    }

    [Fact]
    public void Five_squares_meet_at_every_corner()
    {
        double h = HyperbolicTiling.KleinHalfSide;
        Complex[] corners = new[] { new Complex(h, h), new Complex(-h, h), new Complex(-h, -h), new Complex(h, -h) }
            .Select(HyperbolicTiling.KleinToDisk).ToArray();
        Complex vertex = corners[0];

        int meeting = HyperbolicTiling.Tiles(maxRadius: 0.9)
            .Count(t => corners.Any(c => (t.Inverse.Apply(vertex) - c).Magnitude < 1e-7));
        Assert.Equal(HyperbolicTiling.CornerValence, meeting);
    }

    [Fact]
    public void Right_down_left_up_does_not_come_home()
    {
        Mobius loop = HyperbolicTiling.StepRight.Then(HyperbolicTiling.StepDown)
            .Then(HyperbolicTiling.StepRight.Inverse).Then(HyperbolicTiling.StepDown.Inverse);
        Assert.True(loop.Centre.Magnitude > 0.1);
    }

    [Fact]
    public void Every_grid_cell_round_the_anchor_lands_on_its_own_tile()
    {
        var centres = new List<Complex>();
        for (int dx = -4; dx <= 4; dx++)
            for (int dy = -4; dy <= 4; dy++)
                centres.Add(HyperbolicTiling.Place(dx, dy).Centre);
        for (int i = 0; i < centres.Count; i++)
            for (int j = i + 1; j < centres.Count; j++)
                Assert.True((centres[i] - centres[j]).Magnitude > 1e-9, $"cells {i} and {j} share a tile");
    }

    [Fact]
    public void Each_placed_room_sits_exactly_on_a_tile_of_the_tiling()
    {
        var tiles = HyperbolicTiling.Tiles(maxRadius: 0.999);
        foreach ((int dx, int dy) in new[] { (1, 0), (0, -1), (2, 1), (-1, 2), (3, -2) })
        {
            Complex c = HyperbolicTiling.Place(dx, dy).Centre;
            Assert.Contains(tiles, t => (t.Centre - c).Magnitude < 1e-7);
        }
    }

    [Fact]
    public void A_point_is_in_the_base_tile_only_inside_its_edges()
    {
        double mid = HyperbolicTiling.KleinToDisk(new Complex(HyperbolicTiling.KleinHalfSide, 0)).Real;
        Assert.True(HyperbolicTiling.InBaseTile(new Complex(mid * 0.99, 0)));
        Assert.False(HyperbolicTiling.InBaseTile(new Complex(mid * 1.01, 0)));
    }

    [Theory]
    [InlineData(0.0, 1, 0, 1, 0)]
    [InlineData(0.0, 0, -1, 0, -1)]
    [InlineData(Math.PI / 2, 1, 0, 0, -1)]   // a quarter-turn clockwise: grid up now points right
    [InlineData(Math.PI / 2, 0, 1, 1, 0)]
    [InlineData(Math.PI, -1, 0, 1, 0)]
    [InlineData(-Math.PI / 2, 0, -1, 1, 0)]  // anticlockwise: grid right now points up
    [InlineData(0.2, 1, 0, 1, 0)]            // a nudge snaps back to the nearest edge
    public void Arrows_turn_with_the_spin(double spin, int sx, int sy, int gx, int gy)
    {
        Assert.Equal((gx, gy), HyperbolicTiling.GridStep(spin, sx, sy));
    }

    // ── Motion ───────────────────────────────────────────────────────────────

    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static SpatialScene Row(int count)
    {
        var rooms = Enumerable.Range(0, count)
            .Select(i => new SpatialRoom(D(i), $"d{i}", new GridPos(i, 0), Guid.Empty, true, false, false, 1)).ToList();
        return new SpatialScene(new[] { new SpatialGroup(Guid.Empty, "main", "#F4795B", true, rooms.Select(r => r.Id).ToList()) }, rooms);
    }

    [Fact]
    public void A_glide_starts_on_what_was_shown_and_ends_on_the_new_layout()
    {
        SpatialScene scene = Row(3);
        var motion = new HyperbolicMotion();
        motion.Snap(HyperbolicTiling.Layout(scene, D(0)), 0);
        Near(Complex.Zero, motion.Room(D(0)).Centre);

        motion.Retarget(HyperbolicTiling.Layout(scene, D(1)), D(1), 0);
        Assert.True(motion.Moving);
        Near(HyperbolicTiling.Place(1, 0).Centre, motion.Room(D(1)).Centre);
        Near(HyperbolicTiling.Place(1, 0).Centre, motion.Background.Centre); // the tiling rides with the new anchor

        motion.Step(10);
        Assert.False(motion.Moving);
        Near(Complex.Zero, motion.Room(D(1)).Centre);
        Near(Complex.Zero, motion.Background.Centre);
    }

    [Fact]
    public void Retargeting_to_the_same_layout_does_not_restart_the_glide()
    {
        SpatialScene scene = Row(2);
        var motion = new HyperbolicMotion();
        motion.Snap(HyperbolicTiling.Layout(scene, D(0)), 0);
        motion.Retarget(HyperbolicTiling.Layout(scene, D(0)), D(0), 0);
        Assert.False(motion.Moving);
    }
}
