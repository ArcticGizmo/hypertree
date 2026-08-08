using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>One drawable group hull: the rectilinear outline of a single touching clump of a group's cells.
/// <see cref="Loops"/> is the outer ring first, then any holes (a group that encircles an empty cell); each
/// ring is a closed list of world-space points. <see cref="Bounds"/> is the ring's world bounding box, for
/// the name badge and framing.</summary>
public sealed record HullShape(IReadOnlyList<IReadOnlyList<LayoutPoint>> Loops, LayoutRect Bounds);

/// <summary>
/// Turns a set of occupied grid cells into <b>polyomino outlines</b> — the "tetris piece" hulls the spatial
/// map draws around a group. Cells that share an edge merge into one ring (the shared edge dissolves), and
/// cells that touch only at a corner (a diagonal) are joined by a short solid <b>corridor</b> so the group
/// reads as one connected piece. Each ring hugs the cells and is pulled inward by an inset so it clears the
/// room tiles inside and neighbouring groups outside.
///
/// Cells are rasterised onto a finer sub-grid (<see cref="Sub"/> sub-cells per cell per axis): a cell fills
/// a solid block, so edge-adjacent blocks abut and merge; a diagonal-only touch — the two cells kitty-corner
/// with both orthogonal neighbours empty — gets a small solid block dropped over the shared corner, which is
/// the corridor. The outline of the sub-grid is then a clean rectilinear polygon with no pinch points. Pure
/// geometry, no Avalonia — the painter turns the rings into a rounded path at the drawing edge.
/// </summary>
public static class SpatialHull
{
    private const int Sub = 16;       // sub-cells per cell per axis — the resolution the diagonal corridor is cut at
    private const int BridgeHalf = 3; // corridor half-width in sub-cells (a 2·BridgeHalf block over the shared corner)

    public static IReadOnlyList<HullShape> Shapes(
        IReadOnlyCollection<GridPos> cells, double strideX, double strideY, double insetX, double insetY)
    {
        var set = new HashSet<(int X, int Y)>();
        foreach (GridPos c in cells) set.Add((c.X, c.Y));
        if (set.Count == 0) return Array.Empty<HullShape>();

        double subX = strideX / Sub, subY = strideY / Sub, offX = strideX / 2, offY = strideY / 2;

        var shapes = new List<HullShape>();
        foreach (HashSet<(int X, int Y)> comp in Components(set))
        {
            HashSet<(int X, int Y)> subCells = Rasterise(comp);

            var loops = new List<IReadOnlyList<LayoutPoint>>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (List<LayoutPoint> ring in Rings(subCells, subX, subY, offX, offY, insetX, insetY))
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
    // diagonally-linked clump reads as one connected piece.
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

    // Paint the component onto the sub-grid: a solid Sub×Sub block per cell, plus a small solid block over the
    // shared corner of each diagonal-only touch — the corridor that joins two kitty-corner rooms. The corridor
    // is only for a genuine diagonal touch: if either cell between them is filled, they already join by an
    // edge and a bridge would wrongly fill the concave notch (e.g. an L).
    private static HashSet<(int X, int Y)> Rasterise(HashSet<(int X, int Y)> comp)
    {
        var sub = new HashSet<(int X, int Y)>();
        foreach ((int x, int y) in comp)
            for (int i = 0; i < Sub; i++)
                for (int j = 0; j < Sub; j++)
                    sub.Add((x * Sub + i, y * Sub + j));

        foreach ((int x, int y) in comp)
            foreach (int dy in new[] { -1, 1 })
            {
                if (!comp.Contains((x + 1, y + dy))) continue;                 // not a diagonal pair
                if (comp.Contains((x + 1, y)) || comp.Contains((x, y + dy))) continue; // already edge-joined

                // Drop a solid block of sub-cells straddling the rooms' shared sub-corner: the two rooms sit at
                // opposite corners of it, so filling it joins them with a corridor of width ~2·BridgeHalf cells.
                int cx = (x + 1) * Sub, cy = (dy > 0 ? y + 1 : y) * Sub;
                for (int i = -BridgeHalf; i < BridgeHalf; i++)
                    for (int j = -BridgeHalf; j < BridgeHalf; j++)
                        sub.Add((cx + i, cy + j));
            }
        return sub;
    }

    // Trace the boundary of the sub-grid region into closed rings of world-space points (inset applied),
    // collinear runs merged so each ring is the minimal set of corners. A sub-cell (x,y) owns the corner box
    // (x,y)..(x+1,y+1); a side is a boundary edge when the neighbour across it is empty. Edges run clockwise
    // around each sub-cell (screen y-down), so the outer ring comes out clockwise and holes anticlockwise.
    private static IEnumerable<List<LayoutPoint>> Rings(
        HashSet<(int X, int Y)> region, double subX, double subY, double offX, double offY, double insetX, double insetY)
    {
        var outgoing = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        void Add((int, int) a, (int, int) b)
        {
            if (!outgoing.TryGetValue(a, out List<(int, int)>? ends)) outgoing[a] = ends = new List<(int, int)>();
            ends.Add(b);
        }
        foreach ((int x, int y) in region)
        {
            if (!region.Contains((x, y - 1))) Add((x, y), (x + 1, y));         // top    →
            if (!region.Contains((x + 1, y))) Add((x + 1, y), (x + 1, y + 1)); // right  ↓
            if (!region.Contains((x, y + 1))) Add((x + 1, y + 1), (x, y + 1)); // bottom ←
            if (!region.Contains((x - 1, y))) Add((x, y + 1), (x, y));         // left   ↑
        }

        while (outgoing.Count > 0)
        {
            // Start on a single-exit corner so the first step is unambiguous; take the sharpest-left turn at
            // any corner that still offers a choice, which keeps the trace on the outside of the region.
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
                if (ends.Count > 1 && prev is { } p)
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
            if (ring.Count >= 4) yield return Inset(ring, subX, subY, offX, offY, insetX, insetY);
        }
    }

    // Sub-corner (cx, cy) → world. offX/offY put a cell's own sub-block symmetric about the room centre.
    private static LayoutPoint World(int cx, int cy, double subX, double subY, double offX, double offY)
        => new(cx * subX - offX, cy * subY - offY);

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
        List<(int X, int Y)> ring, double subX, double subY, double offX, double offY, double insetX, double insetY)
    {
        int n = ring.Count;
        var world = new List<LayoutPoint>(n);
        foreach ((int X, int Y) c in ring) world.Add(World(c.X, c.Y, subX, subY, offX, offY));

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
