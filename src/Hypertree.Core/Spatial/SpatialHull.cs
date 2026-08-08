using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>One drawable clump: the rectilinear outline of a single edge-connected block of a group's cells.
/// <see cref="Loops"/> is the outer ring first, then any holes (a block that encircles an empty cell); each
/// ring is a closed list of world-space points. <see cref="Bounds"/> is the ring's world bounding box, for
/// the name badge and framing.</summary>
public sealed record HullShape(IReadOnlyList<IReadOnlyList<LayoutPoint>> Loops, LayoutRect Bounds);

/// <summary>A corridor joining two rooms that touch only at a corner (a diagonal): the two rooms' near
/// corners, in world space. The painter draws a constant-width capsule between them and unions it into the
/// group's hull so the two blocks read as one connected piece.</summary>
public sealed record HullBridge(LayoutPoint A, LayoutPoint B);

/// <summary>
/// Turns a set of occupied grid cells into <b>polyomino outlines</b> — the "tetris piece" hulls the spatial
/// map draws around a group. Cells that share an edge merge into one ring (the shared edge dissolves), via
/// <see cref="Shapes"/>. Cells that touch only at a corner stay separate blocks but are reported as a
/// <see cref="HullBridge"/> by <see cref="Corridors"/>, so the painter can join them with a slim corridor.
///
/// A cell <c>(gx, gy)</c> owns the world box centred on its room, <c>strideX × strideY</c>, so edge-adjacent
/// cells' boxes abut and their shared edge cancels. Each ring is pulled inward by an inset so it clears the
/// room tiles inside and neighbouring groups outside. Pure geometry, no Avalonia.
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

    /// <summary>The corridors to draw for a group: one per genuine diagonal-only touch — two cells kitty-corner
    /// with both cells between them empty (so they aren't already edge-joined, as an L's arm-tips are). Each is
    /// the two rooms' near corners in world space, inset to sit just inside the tiles.</summary>
    public static IReadOnlyList<HullBridge> Corridors(
        IReadOnlyCollection<GridPos> cells, double strideX, double strideY, double insetX, double insetY)
    {
        var set = new HashSet<(int X, int Y)>();
        foreach (GridPos c in cells) set.Add((c.X, c.Y));

        var bridges = new List<HullBridge>();
        foreach ((int x, int y) in set)
            foreach (int dy in new[] { -1, 1 }) // the two right-hand diagonals — each pair seen once
            {
                if (!set.Contains((x + 1, y + dy))) continue;                 // not a diagonal pair
                if (set.Contains((x + 1, y)) || set.Contains((x, y + dy))) continue; // already edge-joined

                // Each room's corner nearest the other, pulled in by the inset so it sits just inside the tile.
                double ax = (x + 0.5) * strideX - insetX;
                double ay = y * strideY + dy * (0.5 * strideY - insetY);
                double bx = (x + 0.5) * strideX + insetX;
                double by = (y + dy) * strideY - dy * (0.5 * strideY - insetY);
                bridges.Add(new HullBridge(new LayoutPoint(ax, ay), new LayoutPoint(bx, by)));
            }
        return bridges;
    }

    // Split the cells into edge-connected (4-neighbour) blocks — one merged outline each.
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
                foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (cells.Contains((nx, ny)) && seen.Add((nx, ny))) { comp.Add((nx, ny)); stack.Push((nx, ny)); }
            }
            yield return comp;
        }
    }

    // Trace the boundary of one block into closed rings of world-space points (inset applied), collinear runs
    // merged so each ring is the minimal set of corners. A cell (x,y) owns the corner box (x,y)..(x+1,y+1); a
    // side is a boundary edge when the neighbour across it is empty. Edges run clockwise around each cell
    // (screen y-down), so the outer ring comes out clockwise and holes anticlockwise.
    private static IEnumerable<List<LayoutPoint>> Rings(
        HashSet<(int X, int Y)> comp, double strideX, double strideY, double insetX, double insetY)
    {
        var outgoing = new Dictionary<(int X, int Y), (int X, int Y)>();
        foreach ((int x, int y) in comp)
        {
            if (!comp.Contains((x, y - 1))) outgoing[(x, y)] = (x + 1, y);         // top    →
            if (!comp.Contains((x + 1, y))) outgoing[(x + 1, y)] = (x + 1, y + 1); // right  ↓
            if (!comp.Contains((x, y + 1))) outgoing[(x + 1, y + 1)] = (x, y + 1); // bottom ←
            if (!comp.Contains((x - 1, y))) outgoing[(x, y + 1)] = (x, y);         // left   ↑
        }

        while (outgoing.Count > 0)
        {
            (int X, int Y) start = outgoing.Keys.First();
            var corners = new List<(int X, int Y)>();
            (int X, int Y) cur = start;
            do
            {
                corners.Add(cur);
                (int X, int Y) next = outgoing[cur];
                outgoing.Remove(cur);
                cur = next;
            } while (cur != start && outgoing.ContainsKey(cur));

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

        var shiftX = new double[n];
        var shiftY = new double[n];
        var horizontal = new bool[n];
        for (int i = 0; i < n; i++)
        {
            LayoutPoint a = world[i], b = world[(i + 1) % n];
            if (Math.Abs(a.Y - b.Y) < 1e-6) { horizontal[i] = true; shiftY[i] = insetY * Math.Sign(b.X - a.X); }
            else { horizontal[i] = false; shiftX[i] = -insetX * Math.Sign(b.Y - a.Y); }
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
