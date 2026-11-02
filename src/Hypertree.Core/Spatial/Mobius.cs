using System.Numerics;

namespace Hypertree.Spatial;

/// <summary>
/// An orientation-preserving isometry of the Poincaré disk, <c>z ↦ (a·z + b) / (b̄·z + ā)</c> with
/// <c>|a|² − |b|² = 1</c>. Screen convention: the real axis points right and the imaginary axis points
/// <b>down</b>, so a positive rotation turns clockwise on screen and the disk maps straight onto pixels.
/// </summary>
public readonly record struct Mobius(Complex A, Complex B)
{
    public static readonly Mobius Identity = new(Complex.One, Complex.Zero);

    public Complex Apply(Complex z) => (A * z + B) / (Complex.Conjugate(B) * z + Complex.Conjugate(A));

    /// <summary><c>this ∘ inner</c> — apply <paramref name="inner"/> first.</summary>
    public Mobius Then(Mobius inner) => Normalise(
        A * inner.A + B * Complex.Conjugate(inner.B),
        A * inner.B + B * Complex.Conjugate(inner.A));

    public Mobius Inverse => new(Complex.Conjugate(A), -B);

    /// <summary>Rotation about the disk's centre by <paramref name="theta"/> radians (clockwise on screen).</summary>
    public static Mobius Rotate(double theta) => new(Complex.FromPolarCoordinates(1, theta / 2), Complex.Zero);

    /// <summary>The translation that carries the centre to <paramref name="p"/> along the geodesic between
    /// them, with no twist.</summary>
    public static Mobius CentreTo(Complex p)
    {
        double k = 1 / Math.Sqrt(1 - p.Magnitude * p.Magnitude);
        return new Mobius(k, p * k);
    }

    /// <summary>Where the centre lands — the image of 0.</summary>
    public Complex Centre => B / Complex.Conjugate(A);

    /// <summary>The twist left after translating the centre back home: <c>this = CentreTo(Centre) ∘ Rotate(Twist)</c>.</summary>
    public double Twist => 2 * A.Phase;

    /// <summary>Interpolate between two isometries: the centre's image glides along the geodesic between
    /// them while the twist turns the short way round. Used to animate tiles between layouts.</summary>
    public static Mobius Lerp(Mobius from, Mobius to, double t)
    {
        Complex p0 = from.Centre, p1 = to.Centre;
        Complex q = CentreTo(p0).Inverse.Apply(p1);
        double m = q.Magnitude;
        Complex p = m < 1e-12 ? p0 : CentreTo(p0).Apply(q / m * Math.Tanh(t * Math.Atanh(Math.Min(m, 1 - 1e-12))));

        double d = Math.IEEERemainder(to.Twist - from.Twist, 2 * Math.PI);
        return CentreTo(p).Then(Rotate(from.Twist + t * d));
    }

    // Composition drifts off |a|² − |b|² = 1 over long chains; pull it back so deep tiles stay exact.
    private static Mobius Normalise(Complex a, Complex b)
    {
        double n = a.Magnitude * a.Magnitude - b.Magnitude * b.Magnitude;
        double k = n > 0 ? 1 / Math.Sqrt(n) : 1;
        return new Mobius(a * k, b * k);
    }
}
