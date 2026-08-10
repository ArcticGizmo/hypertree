using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// Direction-based navigation for the spatial map. A <c>Ctrl+Alt+Arrow</c> chord in spatial mode moves to
/// the nearest room in the pressed direction rather than walking the row model's branch ladder, so the
/// arrangement you laid out is the one you navigate. Pure grid maths over the scene, so it's shared by the
/// live navigation and the interactive map's arrow-select, and unit-tested on its own.
/// </summary>
public static class SpatialNavigation
{
    /// <summary>The nearest room to <paramref name="from"/> in direction (<paramref name="dx"/>,
    /// <paramref name="dy"/>) — a unit step where exactly one axis is non-zero — or null when there's no room
    /// that way (an edge). Rooms that sit exactly on the pressed axis are always preferred over any diagonal:
    /// a diagonal only wins when nothing lines up on the axis at all. Mirrors the interactive map's arrow-select.</summary>
    public static DesktopId? NextInDirection(SpatialScene scene, DesktopId from, int dx, int dy)
    {
        SpatialRoom? cur = null;
        foreach (SpatialRoom r in scene.Rooms) if (r.Id == from) { cur = r; break; }
        if (cur is null) return null;

        SpatialRoom? best = null;
        (int offAxis, int dist) bestScore = (int.MaxValue, int.MaxValue);
        foreach (SpatialRoom r in scene.Rooms)
        {
            if (r.Id == from) continue;
            int ox = r.Pos.X - cur.Pos.X, oy = r.Pos.Y - cur.Pos.Y;
            if (dx != 0 && Math.Sign(ox) != dx) continue;
            if (dy != 0 && Math.Sign(oy) != dy) continue;
            if (dx != 0 && Math.Abs(oy) > Math.Abs(ox)) continue; // keep to the travel axis
            if (dy != 0 && Math.Abs(ox) > Math.Abs(oy)) continue;
            int cross = dx != 0 ? Math.Abs(oy) : Math.Abs(ox);
            // Rank on (is-it-a-diagonal, distance): aligned rooms (cross == 0) beat every diagonal
            // outright, so a diagonal is only chosen when the axis holds nothing.
            (int offAxis, int dist) score = (cross == 0 ? 0 : 1, Math.Abs(ox) + Math.Abs(oy));
            if (score.CompareTo(bestScore) < 0) { bestScore = score; best = r; }
        }
        return best?.Id;
    }

    /// <summary>The group a desktop belongs to in the scene, or <see cref="Guid.Empty"/> (main / not found).
    /// A move whose target is in a different group is the spatial analog of a dive/surface, which is what the
    /// "show before moving" reveal keys off.</summary>
    public static Guid GroupOf(SpatialScene scene, DesktopId id)
    {
        foreach (SpatialRoom r in scene.Rooms) if (r.Id == id) return r.GroupId;
        return Guid.Empty;
    }
}
