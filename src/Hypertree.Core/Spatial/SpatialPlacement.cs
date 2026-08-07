using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// Keeps the spatial grid one-room-per-cell after a move. Free placement means a move (a room, a block, a
/// group, a drag, or a tidy of one group) can land on cells other rooms already hold; rather than let rooms
/// stack invisibly, the mover <b>wins its cells</b> and every other room it lands on is <b>bumped to the
/// nearest free cell</b> — a shove, not a swap, so the thing you deliberately placed stays put and the
/// displacement cascades outward the minimum distance. Pure grid maths, so it's testable and shared by every
/// move path.
/// </summary>
public static class SpatialPlacement
{
    /// <summary>
    /// Resolve overlaps after a move. The <paramref name="moved"/> rooms are fixed anchors (they keep their
    /// cells); any other room sharing a cell with them — or, cascading, with a room already relocated — is
    /// moved to the nearest free cell. Returns the new positions of only the rooms that had to be relocated
    /// (never a moved room, and never a room that was already clear).
    /// </summary>
    public static IReadOnlyDictionary<DesktopId, GridPos> ResolveOverlaps(
        IReadOnlyList<(DesktopId Id, GridPos Pos)> rooms, IReadOnlySet<DesktopId> moved)
    {
        var occupied = new HashSet<GridPos>();
        foreach ((DesktopId id, GridPos pos) in rooms)
            if (moved.Contains(id)) occupied.Add(pos); // the movers are placed first and never yield

        var result = new Dictionary<DesktopId, GridPos>();
        // Process the settled rooms in a deterministic order (top-to-bottom, then left-to-right) so the same
        // collision always resolves the same way.
        foreach ((DesktopId id, GridPos pos) in rooms
                     .Where(r => !moved.Contains(r.Id))
                     .OrderBy(r => r.Pos.Y).ThenBy(r => r.Pos.X))
        {
            if (occupied.Add(pos)) continue;      // its cell was free — it stays
            GridPos free = NearestFree(pos, occupied);
            occupied.Add(free);
            result[id] = free;
        }
        return result;
    }

    // The closest cell to `from` not in `occupied`, searched ring by ring at growing Chebyshev distance, so a
    // bumped room slides the least it can. Deterministic within a ring (scan order fixed).
    private static GridPos NearestFree(GridPos from, HashSet<GridPos> occupied)
    {
        for (int radius = 1; ; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue; // the ring's edge only
                    var cell = new GridPos(from.X + dx, from.Y + dy);
                    if (!occupied.Contains(cell)) return cell;
                }
        }
    }
}
