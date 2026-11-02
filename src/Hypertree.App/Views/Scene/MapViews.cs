using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views.Scene;

/// <summary>Everything a view needs to draw the spatial map once. Only <see cref="Scene"/>, the size, zoom
/// and style are always set; the interactive map adds the callbacks and plumbing, a card backdrop adds a
/// spotlight. <see cref="Anchor"/> is the room the view should centre on, where that means anything.</summary>
internal sealed record MapViewRequest(
    SpatialScene Scene, double Width, double Height, double Scale, MapStyle Style,
    DesktopId? Anchor = null,
    Action<DesktopId>? OnClick = null, Action<DesktopId>? OnActivate = null,
    IList<(DesktopId Id, Rect Rect)>? Hits = null, Guid? SelectedGroup = null,
    IDictionary<DesktopId, Control>? RoomHosts = null, Guid? HoverGroup = null,
    IReadOnlyCollection<DesktopId>? Spotlight = null);

/// <summary>
/// One way of drawing the spatial map — flat, hyperbolic, a vortex. The arrangement (grid positions, groups)
/// is the same in every view; a view only decides where each room lands on screen, how arrows read against
/// that, and any keys of its own. The stage owns the current view, so the map, the card backdrops and the
/// move flow all draw the one you picked.
/// </summary>
internal interface IMapView
{
    /// <summary>Short name for the legend ("flat", "hyperbolic", …).</summary>
    string Name { get; }

    /// <summary>Whether rooms can be dragged — only where pixel travel maps to a fixed cell stride.</summary>
    bool Draggable { get; }

    /// <summary>Legend rows for this view's own keys.</summary>
    IReadOnlyList<(string Key, string Desc)> Keys { get; }

    /// <summary>Entering the view afresh (switched to with <c>h</c>): reset its orientation.</summary>
    void Reset();

    /// <summary>The map is (re)opening: the next render jumps straight to its pose instead of animating.</summary>
    void Snap();

    /// <summary>Draw the interactive map. May advance internal state (start a glide toward a new anchor).</summary>
    Control Render(MapViewRequest request);

    /// <summary>Draw a still — a card backdrop or the move flow — without touching interactive state.</summary>
    Control RenderStill(MapViewRequest request);

    /// <summary>The grid step an arrow key means in this view.</summary>
    (int Dx, int Dy) Arrow(int dx, int dy);

    /// <summary>Handle one of the view's own keys; true when handled (the caller redraws).</summary>
    bool OnKey(Key key, KeyModifiers modifiers);

    /// <summary>True while an animation is running; the caller ticks <see cref="Step"/> and redraws.</summary>
    bool Animating { get; }

    void Step(double seconds);
}

/// <summary>The views on offer and which one is current, cycled with <c>h</c> on the map.</summary>
internal sealed class MapViewSet
{
    private readonly IMapView[] _views;
    private int _current;

    public MapViewSet(MapCamera camera)
    {
        _views = new IMapView[]
        {
            new FlatMapView(camera), new HyperbolicMapView(), new VortexMapView(),
            new FisheyeMapView(), new GlobeMapView(), new RippleMapView(),
        };
    }

    public IMapView Current => _views[_current];
    public IMapView NextView => _views[(_current + 1) % _views.Length];

    public void Advance()
    {
        _current = (_current + 1) % _views.Length;
        Current.Reset();
    }
}

/// <summary>The ordinary flat map: <see cref="SpatialPainter"/> with the shared dead-zone camera.</summary>
internal sealed class FlatMapView(MapCamera camera) : IMapView
{
    public string Name => "flat";
    public bool Draggable => true;
    public IReadOnlyList<(string Key, string Desc)> Keys => Array.Empty<(string, string)>();

    // The cursor may have wandered far while another view was up; centre on it rather than panning from a
    // stale offset.
    public void Reset() => camera.Reframe();
    public void Snap() { }

    public Control Render(MapViewRequest r) => SpatialPainter.Render(r.Scene, r.Width, r.Height, r.Scale, camera,
        r.OnClick, r.OnActivate, r.Hits, r.SelectedGroup, r.Style, r.RoomHosts, r.HoverGroup, r.Spotlight);

    public Control RenderStill(MapViewRequest r) => SpatialPainter.Render(r.Scene, r.Width, r.Height, r.Scale, new MapCamera(),
        style: r.Style, spotlight: r.Spotlight);

    public (int Dx, int Dy) Arrow(int dx, int dy) => (dx, dy);
    public bool OnKey(Key key, KeyModifiers modifiers) => false;
    public bool Animating => false;
    public void Step(double seconds) { }
}

/// <summary>A view turned by <c>q</c>/<c>e</c> — a quarter-turn, or a nudge with <c>⇧</c> — whose arrows
/// follow the turn, so ↑ is always up the screen.</summary>
internal abstract class SpinningMapView : IMapView
{
    private static readonly double QuarterTurn = Math.PI / 2, Nudge = Math.PI / 12;

    protected double SpinTarget { get; private set; }

    public abstract string Name { get; }
    public bool Draggable => false;
    public virtual IReadOnlyList<(string Key, string Desc)> Keys { get; } = new[] { ("q / e", "quarter-turn · ⇧ for a nudge") };

    public virtual void Reset()
    {
        SpinTarget = 0;
        Snap();
    }

    public abstract void Snap();
    public abstract Control Render(MapViewRequest request);
    public abstract Control RenderStill(MapViewRequest request);
    public abstract bool Animating { get; }
    public abstract void Step(double seconds);

    public (int Dx, int Dy) Arrow(int dx, int dy) => HyperbolicTiling.GridStep(SpinTarget, dx, dy);

    public virtual bool OnKey(Key key, KeyModifiers modifiers)
    {
        double step = modifiers.HasFlag(KeyModifiers.Shift) ? Nudge : QuarterTurn;
        switch (key)
        {
            case Key.Q: SpinTarget -= step; return true;
            case Key.E: SpinTarget += step; return true;
            default: return false;
        }
    }

    /// <summary>The room a still centres on: the interactive anchor when it's in this scene, else the
    /// scene's selection, else its first room.</summary>
    protected static DesktopId? StillAnchor(SpatialScene scene, DesktopId? remembered) =>
        remembered is { } a && scene.Rooms.Any(r => r.Id == a) ? a
        : scene.Rooms.FirstOrDefault(r => r.Selected)?.Id ?? scene.Rooms.FirstOrDefault()?.Id;
}

/// <summary>Where a centred view is aimed: the grid point (fractional mid-glide) it centres on and its turn.</summary>
internal readonly record struct GridPose(double U, double V, double Spin)
{
    /// <summary>A grid point relative to this pose, turned with it (clockwise on screen) — the coordinates
    /// a lens works in, with the anchor at 0.</summary>
    public System.Numerics.Complex Relative(double u, double v) =>
        new System.Numerics.Complex(u - U, v - V) * System.Numerics.Complex.FromPolarCoordinates(1, Spin);
}

/// <summary>
/// A curved view centred on the anchor whose whole picture moves as one when the anchor changes — the vortex,
/// the lenses, the globe. It glides its <see cref="GridPose"/> toward the new anchor and turn; a subclass
/// only paints a pose.
/// </summary>
internal abstract class CentredWarpView : SpinningMapView
{
    private const double Glide = 0.3;

    private GridPose _shown, _from, _to;
    private double _t = 1;
    private bool _snap = true;
    private DesktopId? _anchor;

    public override void Snap() => _snap = true;
    public override bool Animating => _t < 1;

    public override void Step(double seconds)
    {
        _t = Math.Min(1, _t + seconds / Glide);
        double k = 1 - Math.Pow(1 - _t, 3);
        _shown = new GridPose(_from.U + (_to.U - _from.U) * k, _from.V + (_to.V - _from.V) * k,
                              _from.Spin + (_to.Spin - _from.Spin) * k);
    }

    public override Control Render(MapViewRequest r)
    {
        _anchor = r.Anchor;
        SpatialRoom? a = r.Scene.Rooms.FirstOrDefault(x => x.Id == r.Anchor);
        var target = new GridPose(a?.Pos.X ?? _to.U, a?.Pos.Y ?? _to.V, SpinTarget);
        if (_snap)
        {
            _shown = _from = _to = target;
            _t = 1;
            _snap = false;
        }
        else if (target != _to)
        {
            bool moved = target.U != _to.U || target.V != _to.V;
            _from = _shown;
            _to = target;
            _t = 0;
            if (moved) Moved(target);
        }
        return Paint(r, _shown, still: false);
    }

    public override Control RenderStill(MapViewRequest r)
    {
        SpatialRoom? a = r.Scene.Rooms.FirstOrDefault(x => x.Id == StillAnchor(r.Scene, r.Anchor ?? _anchor));
        return Paint(r, new GridPose(a?.Pos.X ?? 0, a?.Pos.Y ?? 0, SpinTarget), still: true);
    }

    /// <summary>The anchor moved to a new cell (not just a turn) — a hook for views that react to arrival.</summary>
    protected virtual void Moved(GridPose to) { }

    /// <summary>Draw the map aimed at <paramref name="at"/>; <paramref name="still"/> for backdrops, which
    /// should leave out anything transient.</summary>
    protected abstract Control Paint(MapViewRequest r, GridPose at, bool still);

    /// <summary>Background seam lines along the grid's cell boundaries within <paramref name="reach"/> cells of
    /// the pose, each sampled every <paramref name="step"/> cells through <paramref name="map"/>; a null point
    /// breaks the line (off the visible face).</summary>
    protected static SkiaSharp.SKPath Lattice(GridPose at, int reach, double step, Func<double, double, Point?> map)
    {
        var path = new SkiaSharp.SKPath();
        int ku = (int)Math.Round(at.U), kv = (int)Math.Round(at.V);
        int samples = (int)Math.Ceiling(2 * reach / step);
        for (int k = -reach; k <= reach; k++)
        {
            Trace(path, samples, i => map(ku + k + 0.5, at.V - reach + i * step));
            Trace(path, samples, i => map(at.U - reach + i * step, kv + k + 0.5));
        }
        return path;
    }

    private static void Trace(SkiaSharp.SKPath path, int samples, Func<int, Point?> at)
    {
        bool open = false;
        for (int i = 0; i <= samples; i++)
        {
            if (at(i) is not { } p) { open = false; continue; }
            if (open) path.LineTo((float)p.X, (float)p.Y);
            else { path.MoveTo((float)p.X, (float)p.Y); open = true; }
        }
    }
}
