using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>One drawable group hull: the rectilinear outline of a single edge-connected clump of a group's
/// cells. <see cref="Loops"/> is the outer ring first, then any holes (a group that encircles an empty
/// cell); each ring is a closed list of world-space points. <see cref="Bounds"/> is the ring's world
/// bounding box, for the name badge and framing.</summary>
public sealed record HullShape(IReadOnlyList<IReadOnlyList<LayoutPoint>> Loops, LayoutRect Bounds);

/// <summary>
/// Turns a set of occupied grid cells into <b>polyomino outlines</b> — the "tetris piece" hulls the spatial
/// map draws around a group. Cells that touch — sharing an edge <em>or</em> just a corner — merge into one
/// ring; a shared edge dissolves outright, and a corner-only (diagonal) touch becomes a concave "neck" where
/// the two cells pinch together, so the group reads as one connected piece. Each ring hugs the cells and is
/// pulled inward by an inset so it clears the room tiles inside and neighbouring groups outside.
///
/// A cell <c>(gx, gy)</c> owns the world box centred on its room, <c>strideX × strideY</c>, so edge-adjacent
/// cells' boxes abut exactly and their shared edge cancels. Pure geometry, no Avalonia — the painter turns
/// the rings into a rounded path at the drawing edge.
/// </summary>
public static class SpatialHull
{
    public static IReadOnlyList<HullShape> Shapes(
        IReadOnlyCollection<GridPos> cells, double strideX, double strideY, double insetX, double insetY)
    {
        var set = new HashSet<(int X, int Y)>();
        foreach (GridPos c in cells) set.Add((c.X, c.Y));
        if (set.Count == 0) return Array.Empty<HullShape>();

        var shapes = new List<HullShape>();
        foreach (HashSet<(int X, int Y)> comp in Components(set))
        {
            var loops = new List<IReadOnlyList<LayoutPoint>>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (List<LayoutPoint> ring in Rings(comp, strideX, strideY, insetX, insetY))
            {
                loops.Add(ring);
                foreach (LayoutPoint p in ring)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
            }
            if (loops.Count == 0) continue;
            shapes.Add(new HullShape(loops, new LayoutRect(minX, minY, maxX - minX, maxY - minY)));
        }
        return shapes;
    }

    // Split the cells into touching (8-neighbour, diagonals included) components — one drawn shape each, so a
    // diagonally-adjacent clump reads as one connected piece.
    private static IEnumerable<HashSet<(int X, int Y)>> Components(HashSet<(int X, int Y)> cells)
    {
        var seen = new HashSet<(int, int)>();
        foreach ((int X, int Y) start in cells)
        {
            if (!seen.Add(start)) continue;
            var comp = new HashSet<(int X, int Y)> { start };
            var stack = new Stack<(int X, int Y)>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                (int x, int y) = stack.Pop();
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        (int nx, int ny) = (x + dx, y + dy);
                        if (cells.Contains((nx, ny)) && seen.Add((nx, ny))) { comp.Add((nx, ny)); stack.Push((nx, ny)); }
                    }
            }
            yield return comp;
        }
    }

    // Trace the boundary of one component into closed rings of world-space points (inset applied), collinear
    // runs merged so each ring is the minimal set of corners. A cell (x,y) owns the corner box (x,y)..(x+1,y+1);
    // a side is a boundary edge when the neighbour across it is empty. Edges run clockwise around each cell
    // (screen y-down), so the outer ring comes out clockwise and holes anticlockwise.
    //
    // A diagonal touch makes a "pinch" corner where two cells meet at a point — that corner has two ways out.
    // We start each ring on an unambiguous (single-exit) corner and, at a pinch, take the sharpest left turn,
    // which hugs the outside of both cells so the pinch becomes a concave neck rather than splitting them.
    private static IEnumerable<List<LayoutPoint>> Rings(
        HashSet<(int X, int Y)> comp, double strideX, double strideY, double insetX, double insetY)
    {
        // Directed unit edges start -> end, keyed by start corner.
        var outgoing = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        void Add((int, int) a, (int, int) b)
        {
            if (!outgoing.TryGetValue(a, out List<(int, int)>? ends)) outgoing[a] = ends = new List<(int, int)>();
            ends.Add(b);
        }
        foreach ((int x, int y) in comp)
        {
            if (!comp.Contains((x, y - 1))) Add((x, y), (x + 1, y));         // top    →
            if (!comp.Contains((x + 1, y))) Add((x + 1, y), (x + 1, y + 1)); // right  ↓
            if (!comp.Contains((x, y + 1))) Add((x + 1, y + 1), (x, y + 1)); // bottom ←
            if (!comp.Contains((x - 1, y))) Add((x, y + 1), (x, y));         // left   ↑
        }

        while (outgoing.Count > 0)
        {
            // Start on a single-exit corner so the first step is unambiguous (a pinch corner is never a start).
            (int X, int Y) startCorner = outgoing.FirstOrDefault(kv => kv.Value.Count == 1).Key;
            if (!outgoing.ContainsKey(startCorner)) startCorner = outgoing.Keys.First();

            var corners = new List<(int X, int Y)>();
            (int X, int Y) cur = startCorner;
            (int X, int Y)? prev = null;
            while (true)
            {
                corners.Add(cur);
                List<(int X, int Y)> ends = outgoing[cur];

                int pick = ends.Count - 1;
                if (ends.Count > 1 && prev is { } p) // a pinch: keep to the outside via the sharpest left turn
                {
                    (int dx, int dy) = (cur.X - p.X, cur.Y - p.Y);
                    double best = double.MaxValue;
                    for (int k = 0; k < ends.Count; k++)
                    {
                        (int ex, int ey) = (ends[k].X - cur.X, ends[k].Y - cur.Y);
                        double cross = dx * ey - dy * ex; // y-down: most-negative = sharpest left
                        if (cross < best) { best = cross; pick = k; }
                    }
                }

                (int X, int Y) next = ends[pick];
                ends.RemoveAt(pick);
                if (ends.Count == 0) outgoing.Remove(cur);
                prev = cur;
                cur = next;
                if (cur == startCorner) break;
            }

            List<(int X, int Y)> ring = DropCollinear(corners);
            if (ring.Count >= 4) yield return Inset(ring, strideX, strideY, insetX, insetY);
        }
    }

    // Corner (cx, cy) → world: the cell centre gx*stride sits at corner gx+0.5, so world = (corner-0.5)*stride.
    private static LayoutPoint World(int cx, int cy, double strideX, double strideY)
        => new((cx - 0.5) * strideX, (cy - 0.5) * strideY);

    // Remove a corner whose neighbours are collinear with it — that's a dissolved shared edge, the whole point.
    private static List<(int X, int Y)> DropCollinear(List<(int X, int Y)> pts)
    {
        int n = pts.Count;
        var keep = new List<(int X, int Y)>(n);
        for (int i = 0; i < n; i++)
        {
            (int X, int Y) a = pts[(i - 1 + n) % n], b = pts[i], c = pts[(i + 1) % n];
            bool collinear = (a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y);
            if (!collinear) keep.Add(b);
        }
        return keep;
    }

    // Pull the ring inward by (insetX, insetY): each edge shifts toward the interior along its normal (interior
    // is on the right of a clockwise ring in y-down), then every vertex is re-derived as the meeting of its now
    // shifted horizontal and vertical edges. Handles concave corners; holes shift the other way via their
    // reversed winding, which is what we want.
    private static List<LayoutPoint> Inset(
        List<(int X, int Y)> ring, double strideX, double strideY, double insetX, double insetY)
    {
        int n = ring.Count;
        var world = new List<LayoutPoint>(n);
        foreach ((int X, int Y) c in ring) world.Add(World(c.X, c.Y, strideX, strideY));

        // Per edge i (world[i] → world[i+1]): a horizontal edge shifts in Y, a vertical edge shifts in X.
        var shiftX = new double[n];
        var shiftY = new double[n];
        var horizontal = new bool[n];
        for (int i = 0; i < n; i++)
        {
            LayoutPoint a = world[i], b = world[(i + 1) % n];
            if (Math.Abs(a.Y - b.Y) < 1e-6) // horizontal: N.y = sign(dx)
            {
                horizontal[i] = true;
                shiftY[i] = insetY * Math.Sign(b.X - a.X);
            }
            else // vertical: N.x = -sign(dy)
            {
                horizontal[i] = false;
                shiftX[i] = -insetX * Math.Sign(b.Y - a.Y);
            }
        }

        var result = new List<LayoutPoint>(n);
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n; // the two edges meeting at vertex i alternate H/V
            LayoutPoint p = world[i];
            double x = horizontal[i] ? p.X + shiftX[prev] : p.X + shiftX[i];
            double y = horizontal[i] ? p.Y + shiftY[i] : p.Y + shiftY[prev];
            result.Add(new LayoutPoint(x, y));
        }
        return result;
    }
}
