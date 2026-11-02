using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Hypertree.Spatial;
using SkiaSharp;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// A fisheye lens (see <see cref="FisheyeMap"/>): the room you're on magnified, its neighbours bulging round it,
/// and the rest of the infinite grid squeezed toward a rim it never reaches. Zoom (<c>+</c>/<c>−</c>) sets the
/// magnification.
/// </summary>
internal sealed class FisheyeMapView : CentredWarpView
{
    private static readonly SKColor Lens = SKColor.Parse("#18202B");
    private static readonly SKColor Seam = SKColor.Parse("#0C1017");
    private static readonly SKColor Rim = SKColor.Parse("#6EA8FF").WithAlpha(90);
    private const string Caption = "your desktops through a fisheye · everything fits if you squint";
    private const double LensFill = 0.47, Magnification = 0.5;

    public override string Name => "fisheye";

    protected override Control Paint(MapViewRequest r, GridPose at, bool still)
    {
        double radius = LensFill * Math.Min(r.Width, r.Height), cx = r.Width / 2, cy = r.Height / 2;
        double mag = Magnification * r.Scale;
        Point Map(double u, double v)
        {
            Complex w = FisheyeMap.Map(at.Relative(u, v), mag);
            return new Point(cx + radius * w.Real, cy + radius * w.Imaginary);
        }

        var tiles = r.Scene.Rooms
            .Select(room => new WarpTile(room, (s, t) => Map(room.Pos.X - 0.5 + s, room.Pos.Y - 0.5 + t)))
            .ToList();
        var lens = new SKPath();
        lens.AddCircle((float)cx, (float)cy, (float)radius);

        return WarpPainter.Render(r, tiles,
            under: new[] { new WarpLayer(lens, Lens) },
            over: new[] { new WarpLayer(Lattice(at, 24, 0.2, (u, v) => Map(u, v)), Seam, 1.2f), new WarpLayer(lens, Rim, 1.5f) },
            Caption);
    }
}

/// <summary>
/// The grid on the Riemann sphere (see <see cref="GlobeMap"/>): every row and column is a circle through ∞,
/// which is just a point you can look at. <c>w</c>/<c>s</c> swing ∞ toward or away from you.
/// </summary>
internal sealed class GlobeMapView : CentredWarpView
{
    private static readonly SKColor Sphere = SKColor.Parse("#1A222E");
    private static readonly SKColor Seam = SKColor.Parse("#0C1017");
    private static readonly SKColor Rim = SKColor.Parse("#6EA8FF").WithAlpha(110);
    private static readonly SKColor Pole = SKColor.Parse("#E6EDF7");
    private const string Caption = "your desktops on the Riemann sphere · every row and column meets at ∞";
    private const double SphereFill = 0.44, Cell = 0.62;
    private const double MinReach = 0.6, MaxReach = 2.4, ReachStep = 0.2, DefaultReach = 1.3;

    // A tile only draws when its whole square faces the eye; one straddling the rim would fold over itself.
    private const double MinFacing = 0.04;

    private double _reach = DefaultReach;

    public override string Name => "Riemann sphere";

    public override IReadOnlyList<(string Key, string Desc)> Keys { get; } = new[]
    {
        ("q / e", "quarter-turn · ⇧ for a nudge"),
        ("w / s", "swing ∞ toward / away"),
    };

    public override void Reset()
    {
        _reach = DefaultReach;
        base.Reset();
    }

    public override bool OnKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.W: _reach = Math.Min(MaxReach, _reach + ReachStep); return true;
            case Key.S: _reach = Math.Max(MinReach, _reach - ReachStep); return true;
            default: return base.OnKey(key, modifiers);
        }
    }

    protected override Control Paint(MapViewRequest r, GridPose at, bool still)
    {
        double radius = SphereFill * Math.Min(r.Width, r.Height), cx = r.Width / 2, cy = r.Height / 2;
        var globe = new GlobeMap(Cell * r.Scale, _reach, at.Spin);
        Point ToScreen(Complex w) => new(cx + radius * w.Real, cy + radius * w.Imaginary);
        (Point At, double Facing) Map(double u, double v)
        {
            (Complex w, double facing) = globe.Map(new Complex(u - at.U, v - at.V));
            return (ToScreen(w), facing);
        }

        var tiles = new List<WarpTile>();
        foreach (SpatialRoom room in r.Scene.Rooms)
        {
            double u0 = room.Pos.X - 0.5, v0 = room.Pos.Y - 0.5;
            if (Map(u0, v0).Facing < MinFacing || Map(u0 + 1, v0).Facing < MinFacing ||
                Map(u0, v0 + 1).Facing < MinFacing || Map(u0 + 1, v0 + 1).Facing < MinFacing) continue;
            tiles.Add(new WarpTile(room, (s, t) => Map(u0 + s, v0 + t).At));
        }

        // Grid lines run to ∞ in both directions, so sample them along a tangent so their ends reach the pole.
        var seams = new SKPath();
        int ku = (int)Math.Round(at.U), kv = (int)Math.Round(at.V);
        const double Edge = Math.PI / 2 - 1e-3;
        for (int k = -30; k <= 30; k++)
        {
            TraceFront(seams, t => Map(ku + k + 0.5, at.V + 3 * Math.Tan(t)), Edge);
            TraceFront(seams, t => Map(at.U + 3 * Math.Tan(t), kv + k + 0.5), Edge);
        }

        var sphere = new SKPath();
        sphere.AddCircle((float)cx, (float)cy, (float)radius);
        var over = new List<WarpLayer> { new(seams, Seam, 1.1f), new(sphere, Rim, 1.5f) };
        (Complex pole, double poleFacing) = globe.Infinity;
        if (poleFacing > 0)
        {
            Point p = ToScreen(pole);
            var dot = new SKPath();
            dot.AddCircle((float)p.X, (float)p.Y, 5);
            over.Add(new WarpLayer(dot, Pole));
        }

        return WarpPainter.Render(r, tiles, under: new[] { new WarpLayer(sphere, Sphere) }, over, Caption);
    }

    private static void TraceFront(SKPath path, Func<double, (Point At, double Facing)> line, double edge)
    {
        const int Samples = 160;
        bool open = false;
        for (int i = 0; i <= Samples; i++)
        {
            (Point p, double facing) = line(-edge + 2 * edge * i / Samples);
            if (facing <= 0) { open = false; continue; }
            if (open) path.LineTo((float)p.X, (float)p.Y);
            else { path.MoveTo((float)p.X, (float)p.Y); open = true; }
        }
    }
}

/// <summary>
/// The flat map seen through water (see <see cref="RippleField"/>). Every move drops a ripple where you land;
/// it spreads, wobbles the rooms and the grid it passes, and fades — then the water goes still and the view
/// stops animating.
/// </summary>
internal sealed class RippleMapView : CentredWarpView
{
    private static readonly SKColor Water = SKColor.Parse("#122030");
    private static readonly SKColor Seam = SKColor.Parse("#2B4A66").WithAlpha(120);
    private const string Caption = "your desktops, underwater · every move makes a splash";
    private const double Stride = 210;

    private readonly RippleField _ripples = new();

    public override string Name => "ripple";
    public override bool Animating => base.Animating || _ripples.Active;

    public override void Step(double seconds)
    {
        base.Step(seconds);
        _ripples.Advance(seconds);
    }

    protected override void Moved(GridPose to) => _ripples.Drop(new Complex(to.U, to.V));

    // Turning drops one too, at the centre, so the water reacts to every press.
    public override bool OnKey(Key key, KeyModifiers modifiers)
    {
        bool handled = base.OnKey(key, modifiers);
        if (handled) _pendingSplash = true;
        return handled;
    }

    private bool _pendingSplash;

    protected override Control Paint(MapViewRequest r, GridPose at, bool still)
    {
        if (_pendingSplash && !still)
        {
            _ripples.Drop(new Complex(at.U, at.V));
            _pendingSplash = false;
        }

        double stride = Stride * r.Scale, cx = r.Width / 2, cy = r.Height / 2;
        Point Map(double u, double v)
        {
            var p = new Complex(u, v);
            if (!still) p += _ripples.Displace(p);
            Complex d = at.Relative(p.Real, p.Imaginary) * stride;
            return new Point(cx + d.Real, cy + d.Imaginary);
        }

        // Rooms sit inset in their cells, as on the flat map, so the water shows between them.
        const double Inset = 0.09;
        var tiles = r.Scene.Rooms
            .Select(room => new WarpTile(room, (s, t) =>
                Map(room.Pos.X - 0.5 + Inset + s * (1 - 2 * Inset), room.Pos.Y - 0.5 + Inset + t * (1 - 2 * Inset))))
            .ToList();

        int reach = (int)Math.Ceiling(Math.Max(r.Width, r.Height) / stride) + 2;
        var water = new SKPath();
        water.AddRect(SKRect.Create(0, 0, (float)r.Width, (float)r.Height));

        return WarpPainter.Render(r, tiles,
            under: new[] { new WarpLayer(water, Water.WithAlpha(170)), new WarpLayer(Lattice(at, reach, 0.1, (u, v) => Map(u, v)), Seam, 1f) },
            over: Array.Empty<WarpLayer>(),
            Caption);
    }
}
