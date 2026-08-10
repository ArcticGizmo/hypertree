using Hypertree.Desktops;
using Hypertree.Layout;

namespace Hypertree.Spatial;

/// <summary>A room placed in world space — its scene data plus the rect it occupies.</summary>
public sealed record PlacedRoom(SpatialRoom Room, LayoutRect Rect);

/// <summary>A group's hull: the rectilinear "tetris" outlines of its rooms (<see cref="Loops"/> — one ring per
/// edge-connected block, edge-adjacent rooms already merged, holes included). <see cref="Rect"/> is the
/// top-most / left-most block's bounds, for the name badge.</summary>
public sealed record GroupHull(
    SpatialGroup Group, IReadOnlyList<IReadOnlyList<LayoutPoint>> Loops, LayoutRect Rect);

/// <summary>
/// Where every room sits in <b>world space</b> for the spatial map — an <see cref="ICameraLayout"/> so the
/// shared <see cref="MapCamera"/> frames and pans it. A room at grid <c>(gx, gy)</c> is centred at
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

    /// <summary>One hull per group: the "tetris" outlines of its rooms (edge-adjacent rooms merged, held
    /// <paramref name="padX"/> / <paramref name="padY"/> clear of the tiles). Corner-only (diagonal) touches
    /// stay separate blocks. The name badge hangs off the top-most / left-most block.</summary>
    public IReadOnlyList<GroupHull> Hulls(double padX, double padY)
    {
        // Full stride cells abut, so edge-adjacent cells merge; inset back to leave `pad` around each tile.
        double insetX = Math.Max(1, (_m.CellStride - _m.CellWidth) / 2 - padX);
        double insetY = Math.Max(1, (_m.RowPitch - _m.CellHeight) / 2 - padY);

        var result = new List<GroupHull>();
        foreach (SpatialGroup g in Scene.Groups)
        {
            var cells = _rooms.Where(r => r.Room.GroupId == g.Id).Select(r => r.Room.Pos).ToList();
            if (cells.Count == 0) continue;

            IReadOnlyList<HullShape> shapes = SpatialHull.Shapes(cells, _m.CellStride, _m.RowPitch, insetX, insetY);
            if (shapes.Count == 0) continue;

            // The badge hangs off the top-most (then left-most) block, so it has a stable home.
            HullShape anchor = shapes[0];
            foreach (HullShape s in shapes)
                if (s.Bounds.Top < anchor.Bounds.Top ||
                    (s.Bounds.Top == anchor.Bounds.Top && s.Bounds.Left < anchor.Bounds.Left))
                    anchor = s;

            var loops = shapes.SelectMany(s => s.Loops).ToList();
            result.Add(new GroupHull(g, loops, anchor.Bounds));
        }
        return result;
    }
}
