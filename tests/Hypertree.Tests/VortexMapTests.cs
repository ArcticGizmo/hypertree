using System;
using System.Linq;
using System.Numerics;
using Hypertree.Desktops;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the Droste vortex: the anchor sits at the bottom of the unit ring reading the right way round,
/// rows are rings each <c>e^(2π/N)</c> apart, cells stay square (the map is conformal), and there are
/// always enough spokes that no two columns of a scene land on the same one. Pure — no Avalonia.
/// </summary>
public class VortexMapTests
{
    private const int N = 12;

    private static void Near(Complex expected, Complex actual) =>
        Assert.True((expected - actual).Magnitude < 1e-9, $"expected {expected}, got {actual}");

    [Fact]
    public void The_anchor_sits_at_the_bottom_of_the_unit_ring()
    {
        Near(Complex.ImaginaryOne, VortexMap.Map(3, 2, 3, 2, N, 0));
        Near(-Complex.ImaginaryOne, VortexMap.Map(3, 2, 3, 2, N, Math.PI)); // half a turn puts it on top
    }

    [Fact]
    public void At_the_anchor_grid_right_is_screen_right_and_grid_down_is_screen_down()
    {
        Complex at = VortexMap.Map(0, 0, 0, 0, N, 0);
        Complex right = VortexMap.Map(1e-6, 0, 0, 0, N, 0) - at;
        Complex down = VortexMap.Map(0, 1e-6, 0, 0, N, 0) - at;
        Assert.True(right.Real > 0 && Math.Abs(right.Imaginary) < 1e-9);
        Assert.True(down.Imaginary > 0 && Math.Abs(down.Real) < 1e-9);
    }

    [Fact]
    public void Each_row_down_is_one_ring_further_out()
    {
        double r0 = VortexMap.Map(0, 0, 0, 0, N, 0).Magnitude, r1 = VortexMap.Map(5, 1, 0, 0, N, 0).Magnitude;
        Assert.Equal(Math.Exp(2 * Math.PI / N), r1 / r0, precision: 9);
        Assert.Equal(r1, VortexMap.RingRadius(1, N), precision: 9);
    }

    [Theory]
    [InlineData(0.3, 0.7)]
    [InlineData(-4, 2.5)]
    public void Cells_stay_square_because_the_map_is_conformal(double u, double v)
    {
        const double h = 1e-6;
        Complex at = VortexMap.Map(u, v, 0, 0, N, 0.4);
        Complex du = (VortexMap.Map(u + h, v, 0, 0, N, 0.4) - at) / h;
        Complex dv = (VortexMap.Map(u, v + h, 0, 0, N, 0.4) - at) / h;
        Assert.Equal(du.Magnitude, dv.Magnitude, precision: 5);
        Assert.Equal(0, (du * Complex.Conjugate(dv)).Real, precision: 5); // at right angles
    }

    [Fact]
    public void A_wide_scene_gets_enough_spokes_that_its_columns_never_share_one()
    {
        var rooms = Enumerable.Range(-3, 20)
            .Select(x => new SpatialRoom(new DesktopId(Guid.NewGuid()), "r", new GridPos(x, 0), Guid.Empty, true, false, false, 1))
            .ToList();
        var scene = new SpatialScene(new[] { new SpatialGroup(Guid.Empty, "main", "#F4795B", true, rooms.Select(r => r.Id).ToList()) }, rooms);

        int spokes = VortexMap.Spokes(scene);
        Assert.True(spokes >= 21);
        var spots = rooms.Select(r => VortexMap.Map(r.Pos.X, 0, 0, 0, spokes, 0)).ToList();
        for (int i = 0; i < spots.Count; i++)
            for (int j = i + 1; j < spots.Count; j++)
                Assert.True((spots[i] - spots[j]).Magnitude > 1e-6);
    }
}
