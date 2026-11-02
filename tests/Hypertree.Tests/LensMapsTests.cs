using System;
using System.Numerics;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the lens views' maths: the fisheye squeezes the whole grid inside its rim without folding, the
/// Riemann sphere faces the anchor reading the right way round with ∞ where its reach puts it, and ripples
/// only move the water their front has reached, then fade to stillness. Pure — no Avalonia.
/// </summary>
public class LensMapsTests
{
    [Fact]
    public void The_fisheye_fits_everything_inside_the_rim_and_never_folds()
    {
        double last = 0;
        for (double r = 0.5; r < 30; r *= 1.5) // past ~35 cells tanh rounds to exactly 1 in a double
        {
            double out_ = FisheyeMap.Map(new Complex(r, 0), 0.5).Magnitude;
            Assert.True(out_ < 1);
            Assert.True(out_ > last);
            last = out_;
        }
        Assert.Equal(0.5 * 1e-6, FisheyeMap.Map(new Complex(1e-6, 0), 0.5).Real, precision: 9);
    }

    [Fact]
    public void The_globe_faces_the_anchor_with_right_as_right_and_up_toward_infinity()
    {
        var globe = new GlobeMap(cell: 0.6, reach: 1.3, spin: 0);
        (Complex at, double facing) = globe.Map(Complex.Zero);
        Assert.Equal(0, at.Magnitude, precision: 9);
        Assert.Equal(1, facing, precision: 9);

        Complex right = globe.Map(new Complex(1e-4, 0)).At;
        Complex up = globe.Map(new Complex(0, -1e-4)).At;
        Assert.True(right.Real > 0 && Math.Abs(right.Imaginary) < 1e-3 * right.Real);
        Assert.True(up.Imaginary < 0 && Math.Abs(up.Real) < 1e-3 * -up.Imaginary);

        Complex farUp = globe.Map(new Complex(0, -1e6)).At;
        Assert.True((farUp - globe.Infinity.At).Magnitude < 1e-3); // every line ends at ∞
    }

    [Theory]
    [InlineData(1.0, 0.0)]   // reach 1: ∞ sits on the rim
    [InlineData(2.0, 0.6)]   // further: it climbs onto the near face
    [InlineData(0.5, -0.6)]  // nearer: it hides behind
    public void Reach_sets_where_infinity_shows(double reach, double facing)
    {
        Assert.Equal(facing, new GlobeMap(0.6, reach, 0).Infinity.Facing, precision: 6);
    }

    [Fact]
    public void A_ripple_moves_only_what_its_front_has_reached_then_settles()
    {
        var water = new RippleField();
        Assert.Equal(Complex.Zero, water.Displace(new Complex(1, 0)));

        water.Drop(Complex.Zero);
        water.Advance(0.1);                                       // front at ~0.45 cells
        Assert.Equal(Complex.Zero, water.Displace(new Complex(3, 0)));
        water.Advance(0.5);                                       // front past 2.5 cells
        Assert.NotEqual(Complex.Zero, water.Displace(new Complex(2, 0)));
        Assert.True(water.Active);

        water.Advance(5);
        Assert.False(water.Active);
        Assert.Equal(Complex.Zero, water.Displace(new Complex(2, 0)));
    }
}
