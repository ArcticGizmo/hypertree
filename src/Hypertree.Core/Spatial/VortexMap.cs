using System.Numerics;

namespace Hypertree.Spatial;

/// <summary>
/// The spatial map pushed through the complex exponential — a Droste vortex. Columns become spokes and rows
/// become rings, each ring <c>e^(2π/N)</c> times the one inside it, so the grid spirals down into the centre
/// forever and out past the screen. Because <c>exp</c> is conformal, every cell stays a (curved) square, and
/// because it turns grid translation into rotation-and-zoom, a step along a row spins the vortex and a step
/// between rows zooms one ring — nothing ever re-hangs. Pure maths, shared by the painter and the tests.
/// </summary>
public static class VortexMap
{
    /// <summary>The fewest spokes the vortex has; more when the map is wider, so no two columns share one.</summary>
    public const int MinSpokes = 12;

    /// <summary>How many spokes a scene needs: one per column it spans, plus a gap so the row's two ends don't
    /// meet, and never fewer than <see cref="MinSpokes"/>.</summary>
    public static int Spokes(SpatialScene scene)
    {
        if (scene.Rooms.Count == 0) return MinSpokes;
        int lo = scene.Rooms.Min(r => r.Pos.X), hi = scene.Rooms.Max(r => r.Pos.X);
        return Math.Max(MinSpokes, hi - lo + 2);
    }

    /// <summary>
    /// Where grid point (<paramref name="u"/>, <paramref name="v"/>) lands, relative to the vortex's centre, in
    /// units of the anchor ring's radius. The anchor (<paramref name="u0"/>, <paramref name="v0"/>) sits at the
    /// bottom of the unit ring, turned by <paramref name="spin"/>; grid-right runs anticlockwise round the ring
    /// (rightward at the bottom) and grid-down runs outward. <c>w = e^(iπ/2 + i·spin) · exp(−i·a·(ζ − ζ₀))</c> with <c>ζ = u + iv</c>, <c>a = 2π/N</c>.
    /// </summary>
    public static Complex Map(double u, double v, double u0, double v0, int spokes, double spin)
    {
        double a = 2 * Math.PI / spokes;
        var dz = new Complex(u - u0, v - v0);
        return Complex.FromPolarCoordinates(1, Math.PI / 2 + spin) * Complex.Exp(-Complex.ImaginaryOne * a * dz);
    }

    /// <summary>The ring radius (in anchor-ring units) of the boundary between rows <c>k</c> and <c>k+1</c>,
    /// counted from the anchor's row — the circles drawn as the vortex's seams.</summary>
    public static double RingRadius(double rowsFromAnchor, int spokes) => Math.Exp(2 * Math.PI / spokes * rowsFromAnchor);
}
