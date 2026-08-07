using Hypertree.Desktops;
using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>A room placed in world space — its scene data plus the rect it occupies.</summary>
public sealed record PlacedRoom(SpatialRoom Room, LayoutRect Rect);

/// <summary>A group's hull: the padded bounding rect of one contiguous fragment of its rooms. A group with
/// scattered rooms yields several; <see cref="Primary"/> marks the one the name badge hangs off (the
/// top-most, then left-most, fragment) so the label has a stable home.</summary>
public sealed record GroupHull(SpatialGroup Group, LayoutRect Rect, bool Primary);

/// <summary>
/// Where every room sits in <b>world space</b> for the spatial map — the spatial twin of
/// <see cref="SceneLayout"/>, and an <see cref="ICameraLayout"/> so the shared <see cref="MapCamera"/> frames
/// and pans it exactly as it does the rows. A room at grid <c>(gx, gy)</c> is centred at
/// <c>(gx · CellStride, gy · RowPitch)</c> — the same metrics vocabulary the row layout uses, so one camera
/// serves both. Coordinates are cursor-independent: moving the selection never moves the map (that's the
/// camera's job, and only at the edge).
/// </summary>
public sealed class SpatialLayout : ICameraLayout
{
    private readonly SceneMetrics _m;
    private readonly List<PlacedRoom> _rooms;
    private readonly Dictionary<DesktopId, LayoutRect> _byId;

    public SpatialScene Scene { get; }
    public SceneMetrics Metrics => _m;

    public SpatialLayout(SpatialScene scene, SceneMetrics metrics)
    {
        Scene = scene;
        _m = metrics;
        _rooms = new List<PlacedRoom>(scene.Rooms.Count);
        _byId = new Dictionary<DesktopId, LayoutRect>(scene.Rooms.Count);

        foreach (SpatialRoom r in scene.Rooms)
        {
            double cx = r.Pos.X * _m.CellStride, cy = r.Pos.Y * _m.RowPitch;
            var rect = new LayoutRect(cx - _m.CellWidth / 2, cy - _m.CellHeight / 2, _m.CellWidth, _m.CellHeight);
            _rooms.Add(new PlacedRoom(r, rect));
            _byId[r.Id] = rect;
        }
    }

    public IReadOnlyList<PlacedRoom> Rooms => _rooms;
    public LayoutRect RoomRect(DesktopId id) => _byId[id];

    /// <summary>The selection's world rect — the selected room, else the "here" room, else the first, else a
    /// unit rect at the origin so the camera always has something to frame on an empty map.</summary>
    public LayoutRect SelectionRect
    {
        get
        {
            PlacedRoom? sel = _rooms.FirstOrDefault(r => r.Room.Selected)
                           ?? _rooms.FirstOrDefault(r => r.Room.Here)
                           ?? (_rooms.Count > 0 ? _rooms[0] : null);
            return sel?.Rect ?? new LayoutRect(-_m.CellWidth / 2, -_m.CellHeight / 2, _m.CellWidth, _m.CellHeight);
        }
    }

    public (double Lo, double Hi) WorldX()
    {
        if (_rooms.Count == 0) return (0, 0);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (PlacedRoom r in _rooms) { lo = Math.Min(lo, r.Rect.Left); hi = Math.Max(hi, r.Rect.Right); }
        return (lo, hi);
    }

    public (double Lo, double Hi) WorldY()
    {
        if (_rooms.Count == 0) return (0, 0);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (PlacedRoom r in _rooms) { lo = Math.Min(lo, r.Rect.Top); hi = Math.Max(hi, r.Rect.Bottom); }
        return (lo, hi);
    }

    /// <summary>The group hulls to draw, padded by (<paramref name="padX"/>, <paramref name="padY"/>) around
    /// each contiguous fragment. One hull per fragment, so a split group shows as several; the top-most /
    /// left-most fragment of each group is flagged <see cref="GroupHull.Primary"/> for the name badge.</summary>
    public IReadOnlyList<GroupHull> Hulls(double padX, double padY)
    {
        var result = new List<GroupHull>();
        foreach (SpatialGroup g in Scene.Groups)
        {
            List<PlacedRoom> members = _rooms.Where(r => r.Room.GroupId == g.Id).ToList();
            if (members.Count == 0) continue;

            IReadOnlyList<IReadOnlyList<int>> frags =
                SpatialClusters.Fragments(members.Select(m => m.Room.Pos).ToList());

            var hulls = new List<LayoutRect>(frags.Count);
            foreach (IReadOnlyList<int> frag in frags)
            {
                double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
                foreach (int i in frag)
                {
                    LayoutRect rc = members[i].Rect;
                    l = Math.Min(l, rc.Left); t = Math.Min(t, rc.Top);
                    r = Math.Max(r, rc.Right); b = Math.Max(b, rc.Bottom);
                }
                hulls.Add(new LayoutRect(l - padX, t - padY, r - l + 2 * padX, b - t + 2 * padY));
            }

            // The badge hangs off the top-most (then left-most) fragment, so it has a stable home even as
            // fragments come and go.
            int primary = 0;
            for (int i = 1; i < hulls.Count; i++)
                if (hulls[i].Top < hulls[primary].Top ||
                    (hulls[i].Top == hulls[primary].Top && hulls[i].Left < hulls[primary].Left))
                    primary = i;

            for (int i = 0; i < hulls.Count; i++)
                result.Add(new GroupHull(g, hulls[i], i == primary));
        }
        return result;
    }
}
