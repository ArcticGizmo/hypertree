using System.Numerics;
using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// The spatial map laid on the <b>{4,5} tiling</b> of the hyperbolic plane — squares, five round every corner —
/// drawn in the Poincaré disk. A flat grid can't sit on it faithfully (right, down, left, up doesn't close:
/// the corners have no room for four right angles), so rooms are placed by walking from the room the view is
/// centred on: along its row to the room's column, then a quarter-turn down that column. The anchor's row lies
/// on a geodesic through the centre and every column hangs off it at a right angle; in hyperbolic space those
/// columns diverge and never meet again, so every grid cell lands on its own tile and the anchor's four grid
/// neighbours share an edge with it. The tiles in between stay empty — the fifth square at each corner.
/// Pure geometry so the painter, the animation and the arrow-key remap share it, and it's unit-testable.
/// </summary>
public static class HyperbolicTiling
{
    /// <summary>Squares per corner.</summary>
    public const int CornerValence = 5;

    /// <summary>Hyperbolic distance from a tile's centre to the middle of an edge: cosh ρ = cos(π/5) / sin(π/4).</summary>
    public static readonly double InRadius = Acosh(Math.Cos(Math.PI / CornerValence) / Math.Sin(Math.PI / 4));

    /// <summary>The central tile in the Klein model is a Euclidean square <c>[−h, h]²</c> (Klein geodesics are
    /// straight), which is what makes an exact, gap-free texture mesh easy.</summary>
    public static readonly double KleinHalfSide = Math.Tanh(InRadius);

    /// <summary>One step right: the translation along the real axis that carries a tile onto its neighbour.</summary>
    public static readonly Mobius StepRight = Mobius.CentreTo(new Complex(Math.Tanh(InRadius), 0));

    /// <summary>One step down (screen y grows downward): <see cref="StepRight"/> turned a quarter clockwise.</summary>
    public static readonly Mobius StepDown = Mobius.Rotate(Math.PI / 2).Then(StepRight).Then(Mobius.Rotate(-Math.PI / 2));

    /// <summary>The isometry placing a room <paramref name="dx"/>, <paramref name="dy"/> grid steps from the
    /// anchor: row first, then down the column.</summary>
    public static Mobius Place(int dx, int dy) => Power(StepRight, dx).Then(Power(StepDown, dy));

    /// <summary>Every room's isometry relative to <paramref name="anchor"/>; empty when the anchor isn't in the scene.</summary>
    public static IReadOnlyDictionary<DesktopId, Mobius> Layout(SpatialScene scene, DesktopId anchor)
    {
        var result = new Dictionary<DesktopId, Mobius>(scene.Rooms.Count);
        SpatialRoom? a = scene.Rooms.FirstOrDefault(r => r.Id == anchor);
        if (a is null) return result;
        foreach (SpatialRoom r in scene.Rooms) result[r.Id] = Place(r.Pos.X - a.Pos.X, r.Pos.Y - a.Pos.Y);
        return result;
    }

    /// <summary>Klein → Poincaré: the same point of the plane, moved to the conformal model.</summary>
    public static Complex KleinToDisk(Complex k) => k / (1 + Math.Sqrt(Math.Max(0, 1 - k.Magnitude * k.Magnitude)));

    /// <summary>Poincaré → Klein.</summary>
    public static Complex DiskToKlein(Complex p) => 2 * p / (1 + p.Magnitude * p.Magnitude);

    /// <summary>Whether a disk point lies in the base tile (the one centred at 0).</summary>
    public static bool InBaseTile(Complex p)
    {
        Complex k = DiskToKlein(p);
        return Math.Abs(k.Real) <= KleinHalfSide && Math.Abs(k.Imaginary) <= KleinHalfSide;
    }

    /// <summary>
    /// The whole tiling around the centre, as isometries of the base tile, out to tiles whose centre sits within
    /// <paramref name="maxRadius"/> of the disk's rim (beyond that they're sub-pixel). Breadth-first across
    /// edges, de-duplicated by centre. The tiling is symmetric under every tile isometry, so one enumeration
    /// serves any view that's within a step or so of a tile.
    /// </summary>
    public static IReadOnlyList<Mobius> Tiles(double maxRadius = 0.993, int maxTiles = 4000)
    {
        Mobius[] edges = Enumerable.Range(0, 4)
            .Select(k => Mobius.Rotate(k * Math.PI / 2).Then(StepRight).Then(Mobius.Rotate(-k * Math.PI / 2)))
            .ToArray();
        var seen = new HashSet<(long, long)> { Key(Complex.Zero) };
        var tiles = new List<Mobius> { Mobius.Identity };
        var queue = new Queue<Mobius>(tiles);
        while (queue.Count > 0 && tiles.Count < maxTiles)
        {
            Mobius t = queue.Dequeue();
            foreach (Mobius e in edges)
            {
                Mobius n = t.Then(e);
                Complex c = n.Centre;
                if (c.Magnitude > maxRadius || !seen.Add(Key(c))) continue;
                tiles.Add(n);
                queue.Enqueue(n);
            }
        }
        return tiles;
    }

    /// <summary>The grid step an arrow means when the view is spun by <paramref name="spin"/>: the screen
    /// direction turned back into the tiling's frame and snapped to the nearest of the four edges.</summary>
    public static (int Dx, int Dy) GridStep(double spin, int screenDx, int screenDy)
    {
        double angle = Math.Atan2(screenDy, screenDx) - spin;
        int quarter = (int)Math.Round(angle / (Math.PI / 2));
        return (((quarter % 4) + 4) % 4) switch
        {
            0 => (1, 0),
            1 => (0, 1),
            2 => (-1, 0),
            _ => (0, -1),
        };
    }

    private static Mobius Power(Mobius step, int n)
    {
        Mobius m = Mobius.Identity, s = n >= 0 ? step : step.Inverse;
        for (int i = 0; i < Math.Abs(n); i++) m = m.Then(s);
        return m;
    }

    private static (long, long) Key(Complex c) => ((long)Math.Round(c.Real * 1e6), (long)Math.Round(c.Imaginary * 1e6));

    private static double Acosh(double x) => Math.Log(x + Math.Sqrt(x * x - 1));
}
