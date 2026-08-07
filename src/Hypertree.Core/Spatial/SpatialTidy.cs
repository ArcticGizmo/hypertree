using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// Tidy up — the broom for a spatial map that has drifted. Free placement and deletions let a group split
/// into scattered fragments; Tidy magnets them back together, but crucially it moves each fragment as a
/// <b>rigid block</b> so the little arrangement inside it is preserved, rather than re-squaring everything.
///
/// Two scopes:
/// <list type="bullet">
///   <item><see cref="Group"/> reunites one group's fragments in place (its largest fragment stays put, the
///   others slide against it) — for tidying just the group you're looking at.</item>
///   <item><see cref="All"/> reunites every group, then packs the group blocks onto a shelf so none overlap
///   — a whole-map straighten.</item>
/// </list>
/// Pure geometry over grid positions: it returns the new position for each room that it places, and the
/// caller writes them back and persists. Reversible is the caller's job (it snapshots first).
/// </summary>
public static class SpatialTidy
{
    /// <summary>Reunite one group's fragments into a single contiguous block, anchored where its largest
    /// fragment already sits. Returns the new position of each of that group's rooms.</summary>
    public static IReadOnlyDictionary<DesktopId, GridPos> Group(SpatialScene scene, Guid group)
    {
        var into = new Dictionary<DesktopId, GridPos>();
        Assemble(scene.Rooms.Where(r => r.GroupId == group).ToList(), into);
        return into;
    }

    /// <summary>Reunite every group and pack the resulting blocks so none overlap. Returns the new position
    /// of every room. Groups are processed in scene order; blocks are laid on a left-to-right shelf that
    /// wraps at a roughly-square width, snapped to the grid.</summary>
    public static IReadOnlyDictionary<DesktopId, GridPos> All(SpatialScene scene)
    {
        var result = new Dictionary<DesktopId, GridPos>();
        int shelfX = 0, shelfY = 0, shelfH = 0;
        const int gap = 1;
        int maxW = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, scene.Rooms.Count))) + 4;

        foreach (SpatialGroup g in scene.Groups)
        {
            var members = scene.Rooms.Where(r => r.GroupId == g.Id).ToList();
            if (members.Count == 0) continue;

            // Assemble the group into a local block (shape preserved), then translate the whole block onto
            // the shelf so it can't overlap its neighbours.
            var local = new Dictionary<DesktopId, GridPos>();
            Assemble(members, local);
            (int minX, int minY, int maxX, int maxY) = Box(local.Values);
            int w = maxX - minX + 1, h = maxY - minY + 1;

            if (shelfX > 0 && shelfX + w > maxW) { shelfX = 0; shelfY += shelfH + gap; shelfH = 0; }
            int dx = shelfX - minX, dy = shelfY - minY;
            foreach ((DesktopId id, GridPos p) in local) result[id] = p.Offset(dx, dy);

            shelfX += w + gap;
            shelfH = Math.Max(shelfH, h);
        }
        return result;
    }

    // Reunite a group's fragments: anchor the largest in place, then slide each other fragment rigidly so it
    // abuts the growing block — below when the block is wider than tall, else to the right — keeping it
    // squarish. Each fragment keeps its internal shape (a pure translation). Results written into `into`.
    private static void Assemble(IReadOnlyList<SpatialRoom> members, Dictionary<DesktopId, GridPos> into)
    {
        if (members.Count == 0) return;

        List<IReadOnlyList<int>> frags = SpatialClusters.Fragments(members.Select(m => m.Pos).ToList())
            .OrderByDescending(f => f.Count).ToList();

        GridPos PosOf(int i) => members[i].Pos;

        // Anchor: the largest fragment stays exactly where it is.
        foreach (int i in frags[0]) into[members[i].Id] = PosOf(i);
        (int bx0, int by0, int bx1, int by1) = Box(frags[0].Select(PosOf));

        for (int f = 1; f < frags.Count; f++)
        {
            (int fx0, int fy0, int fx1, int fy1) = Box(frags[f].Select(PosOf));
            int aw = bx1 - bx0 + 1, ah = by1 - by0 + 1;
            int dx, dy;
            if (aw <= ah) { dx = bx1 + 1 - fx0; dy = by0 - fy0; }   // grow rightward
            else { dx = bx0 - fx0; dy = by1 + 1 - fy0; }             // grow downward

            foreach (int i in frags[f]) into[members[i].Id] = PosOf(i).Offset(dx, dy);

            // Expand the running block to include the fragment just placed.
            bx0 = Math.Min(bx0, fx0 + dx); by0 = Math.Min(by0, fy0 + dy);
            bx1 = Math.Max(bx1, fx1 + dx); by1 = Math.Max(by1, fy1 + dy);
        }
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) Box(IEnumerable<GridPos> ps)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (GridPos p in ps)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        return (x0, y0, x1, y1);
    }
}
