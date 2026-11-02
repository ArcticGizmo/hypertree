using System.Numerics;

namespace Hypertree.Spatial;

/// <summary>
/// A fisheye lens: the grid around the anchor magnified, the rest squeezed toward a rim it never reaches.
/// Distance from the anchor goes through <c>tanh</c>, so the whole infinite grid fits in the unit disk with
/// the centre at <paramref name="magnification"/> disk-radii per cell. Pure maths for the painter and tests.
/// </summary>
public static class FisheyeMap
{
    /// <summary>Where a point <paramref name="d"/> cells from the anchor lands, in disk radii.</summary>
    public static Complex Map(Complex d, double magnification)
    {
        double r = d.Magnitude;
        if (r < 1e-12) return d * magnification;
        return d / r * Math.Tanh(r * magnification);
    }
}

/// <summary>
/// The grid on the Riemann sphere: the plane wrapped onto a sphere by inverse stereographic projection, so
/// every row and column becomes a circle through one point — ∞, the north pole. The camera faces the anchor;
/// how far ∞ sits from it is <see cref="Reach"/>: the anchor is lifted from that distance out in the plane, so
/// at 1 the pole sits on the visible rim, above 1 it climbs onto the near face, below 1 it hides behind.
/// Coordinates out are in sphere radii, screen-oriented (x right, y down), orthographic.
/// </summary>
public sealed class GlobeMap
{
    private readonly double _cell;
    private readonly Complex _offset, _turn;
    private readonly (double X, double Y, double Z) _front, _up, _right;

    /// <summary>Plane distance from the stereographic origin to the anchor.</summary>
    public double Reach { get; }

    /// <param name="cell">Plane units per grid cell.</param>
    /// <param name="reach">See <see cref="Reach"/>.</param>
    /// <param name="spin">Rotation of the picture about the anchor, clockwise on screen.</param>
    public GlobeMap(double cell, double reach, double spin)
    {
        _cell = cell;
        Reach = reach;
        _turn = Complex.FromPolarCoordinates(1, -spin);
        // The anchor sits "below" the stereographic origin in grid terms, so grid-up heads away from the origin
        // and toward ∞ — which the frame then puts at the top of the screen.
        _offset = new Complex(0, reach);
        _front = Lift(_offset);
        var north = (X: 0.0, Y: 0.0, Z: 1.0);
        double along = Dot(north, _front);
        _up = Normalise((north.X - along * _front.X, north.Y - along * _front.Y, north.Z - along * _front.Z));
        _right = Cross(_front, _up);
    }

    /// <summary>Where a point <paramref name="d"/> cells from the anchor (grid y down) lands: screen-oriented
    /// position in sphere radii, and how squarely it faces the eye (1 at the anchor, ≤ 0 behind).</summary>
    public (Complex At, double Facing) Map(Complex d)
    {
        Complex turned = d * _turn;
        // Grid y runs down the screen; flip it so grid-up moves away from the origin (toward ∞).
        var p = Lift(_offset + _cell * new Complex(turned.Real, -turned.Imaginary));
        return Project(p);
    }

    /// <summary>∞ — the north pole — on screen.</summary>
    public (Complex At, double Facing) Infinity => Project((0, 0, 1));

    private (Complex At, double Facing) Project((double X, double Y, double Z) p) =>
        (new Complex(Dot(p, _right), -Dot(p, _up)), Dot(p, _front));

    private static (double X, double Y, double Z) Lift(Complex z)
    {
        double m = z.Real * z.Real + z.Imaginary * z.Imaginary;
        return (2 * z.Real / (m + 1), 2 * z.Imaginary / (m + 1), (m - 1) / (m + 1));
    }

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Normalise((double X, double Y, double Z) v)
    {
        double l = Math.Sqrt(Dot(v, v));
        return (v.X / l, v.Y / l, v.Z / l);
    }
}

/// <summary>
/// Ripples on water: each one spreads from where it was dropped as a ring of waves, fading with age, and
/// displaces the grid along its radius. Times are in seconds, distances in cells. Pure state for the ripple
/// view and its tests; the caller supplies the clock.
/// </summary>
public sealed class RippleField
{
    private const double Speed = 4.5;        // cells per second the wavefront travels
    private const double Wavelength = 1.6;   // cells between crests
    private const double Amplitude = 0.16;   // peak displacement, in cells
    private const double Lifetime = 2.6;     // seconds until a ripple has faded out
    private const double FrontWidth = 2.2;   // cells of wave trailing behind the front

    private readonly List<(Complex Centre, double Born)> _ripples = new();

    public double Now { get; private set; }

    /// <summary>True while any ripple is still visible.</summary>
    public bool Active => _ripples.Count > 0;

    public void Drop(Complex centre) => _ripples.Add((centre, Now));

    public void Advance(double seconds)
    {
        Now += seconds;
        _ripples.RemoveAll(r => Now - r.Born >= Lifetime);
    }

    /// <summary>The displacement at grid point <paramref name="p"/>, in cells.</summary>
    public Complex Displace(Complex p)
    {
        Complex total = Complex.Zero;
        foreach ((Complex centre, double born) in _ripples)
        {
            double age = Now - born;
            Complex d = p - centre;
            double r = d.Magnitude;
            double behind = Speed * age - r;          // how far the front has passed this point
            if (r < 1e-9 || behind < 0 || behind > FrontWidth * (1 + age)) continue;
            double fade = (1 - age / Lifetime) * Math.Exp(-behind / (FrontWidth * (1 + age)));
            double wave = Math.Sin(2 * Math.PI * behind / Wavelength) * Amplitude * fade;
            total += d / r * wave;
        }
        return total;
    }
}
