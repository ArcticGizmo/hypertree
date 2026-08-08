using Hypertree.Desktops;
using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>An off-screen room's edge indicator: the point where the straight line from the viewport centre
/// to the room crosses the (inset) viewport border, plus the outward <see cref="Angle"/> in radians. The
/// painter draws an arrow touching the border at <see cref="X"/>,<see cref="Y"/> pointing along
/// <see cref="Angle"/>, with a colour bleed behind it, so an off-screen desktop still tells you which way it
/// lies.</summary>
public readonly record struct EdgeMarker(DesktopId Room, double X, double Y, double Angle);

/// <summary>
/// Pure geometry for the map's off-screen indicators. For every room whose projected rect lies wholly
/// outside the viewport, it finds where the ray from the viewport centre toward that room crosses the border
/// — the arrow's home. Kept out of the painter (and free of Avalonia) so the maths can be reasoned about and
/// tested; the painter only turns each <see cref="EdgeMarker"/> into pixels.
/// </summary>
public static class OffscreenMarkers
{
    /// <summary>
    /// The edge markers for the current frame. <paramref name="offsetX"/>/<paramref name="offsetY"/> are the
    /// camera offsets (<c>screen = world + offset</c>); <paramref name="viewW"/>/<paramref name="viewH"/> the
    /// viewport; <paramref name="inset"/> keeps each marker that far in from the very edge so the arrow reads
    /// as touching the border rather than clipping off it. A room counts as off-screen only when no part of
    /// its rect intersects the viewport — a partly-visible room needs no pointer.
    /// </summary>
    public static IReadOnlyList<EdgeMarker> Compute(SpatialLayout layout, double offsetX, double offsetY,
                                                    double viewW, double viewH, double inset)
    {
        var markers = new List<EdgeMarker>();
        if (viewW <= 0 || viewH <= 0) return markers;

        double cx = viewW / 2, cy = viewH / 2;
        // Clamp the inset so the border box can't collapse (or invert) on a tiny viewport.
        double ins = Math.Min(inset, Math.Min(viewW, viewH) / 2 - 1);
        if (ins < 0) ins = 0;

        foreach (PlacedRoom pr in layout.Rooms)
        {
            LayoutRect r = pr.Rect;
            double left = r.Left + offsetX, top = r.Top + offsetY;
            double right = left + r.Width, bottom = top + r.Height;

            // On screen if any part intersects the viewport — only wholly-clipped rooms get an arrow.
            if (right > 0 && bottom > 0 && left < viewW && top < viewH) continue;

            double px = (left + right) / 2, py = (top + bottom) / 2;
            double dx = px - cx, dy = py - cy;
            if (dx * dx + dy * dy < 1e-9) continue; // room centred on the viewport centre (can't happen off-screen)

            (double bx, double by) = BorderHit(cx, cy, dx, dy, ins, viewW - ins, ins, viewH - ins);
            markers.Add(new EdgeMarker(pr.Room.Id, bx, by, Math.Atan2(dy, dx)));
        }
        return markers;
    }

    // The point where the ray from (cx,cy) in direction (dx,dy) first crosses the box
    // [xMin,xMax]×[yMin,yMax]. The origin is the viewport centre, always inside the box, so the smallest
    // positive parameter across the two bounded axes lands on the border.
    private static (double X, double Y) BorderHit(double cx, double cy, double dx, double dy,
                                                  double xMin, double xMax, double yMin, double yMax)
    {
        double t = double.PositiveInfinity;
        if (dx > 1e-9) t = Math.Min(t, (xMax - cx) / dx);
        else if (dx < -1e-9) t = Math.Min(t, (xMin - cx) / dx);
        if (dy > 1e-9) t = Math.Min(t, (yMax - cy) / dy);
        else if (dy < -1e-9) t = Math.Min(t, (yMin - cy) / dy);
        if (double.IsInfinity(t)) t = 0;
        return (cx + dx * t, cy + dy * t);
    }
}
