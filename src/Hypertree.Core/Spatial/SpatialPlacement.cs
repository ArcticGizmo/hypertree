namespace Hypertree.Spatial;

/// <summary>
/// Where a freshly created desktop lands on the spatial grid: the empty cell <b>closest</b> to an anchor —
/// the room it was created from. Ties at equal distance resolve by a fixed compass priority — right, bottom,
/// left, top, then the diagonals right-bottom, left-bottom, top-right, top-left — so a new room always
/// appears in a predictable spot beside its group rather than at a scattered row-layout default.
/// </summary>
public static class SpatialPlacement
{
    // Equal-distance tie-break order: R, B, L, T, then BR, BL, TR, TL.
    private static readonly (int Dx, int Dy)[] Priority =
    {
        (1, 0), (0, 1), (-1, 0), (0, -1),      // right, bottom, left, top
        (1, 1), (-1, 1), (1, -1), (-1, -1),    // right-bottom, left-bottom, top-right, top-left
    };

    /// <summary>The empty cell nearest <paramref name="anchor"/> that isn't in <paramref name="occupied"/>
    /// (if the anchor itself is free, it's returned). Distance is Euclidean; equal distances break by the
    /// compass <see cref="Priority"/>, then deterministically by (y, x) so the result never depends on
    /// enumeration order.</summary>
    public static GridPos NearestEmpty(GridPos anchor, IReadOnlyCollection<GridPos> occupied)
    {
        var taken = occupied as HashSet<GridPos> ?? new HashSet<GridPos>(occupied);
        if (!taken.Contains(anchor)) return anchor;

        // Expand in square (Chebyshev) rings: any cell in ring r is at least r away, and no cell in a later
        // ring can be Euclidean-closer than the nearest free cell we find here, so the first ring with a free
        // cell holds the winner.
        for (int radius = 1; radius < 256; radius++)
        {
            (int Dx, int Dy)? best = null;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue; // this ring's shell only
                    if (taken.Contains(new GridPos(anchor.X + dx, anchor.Y + dy))) continue;
                    if (best is null || Better((dx, dy), best.Value)) best = (dx, dy);
                }
            if (best is { } b) return new GridPos(anchor.X + b.Dx, anchor.Y + b.Dy);
        }
        return anchor; // grid is full within 256 rings — give up gracefully rather than loop forever
    }

    private static bool Better((int Dx, int Dy) a, (int Dx, int Dy) b)
    {
        int da = a.Dx * a.Dx + a.Dy * a.Dy, db = b.Dx * b.Dx + b.Dy * b.Dy;
        if (da != db) return da < db;                    // closer wins
        int ra = Rank(a.Dx, a.Dy), rb = Rank(b.Dx, b.Dy);
        if (ra != rb) return ra < rb;                    // then the compass priority
        if (a.Dy != b.Dy) return a.Dy < b.Dy;            // then a stable fall-back for off-compass ties
        return a.Dx < b.Dx;
    }

    private static int Rank(int dx, int dy)
    {
        int i = Array.IndexOf(Priority, (Math.Sign(dx), Math.Sign(dy)));
        return i < 0 ? Priority.Length : i;
    }
}
