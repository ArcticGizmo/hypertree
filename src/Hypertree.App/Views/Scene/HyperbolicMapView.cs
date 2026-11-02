using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Hypertree.Desktops;
using Hypertree.Spatial;
using SkiaSharp;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The spatial map on the {4,5} tiling of the hyperbolic plane in the Poincaré disk (see
/// <see cref="HyperbolicTiling"/>). This view only supplies geometry — each room's isometry, the tiling's
/// seams, the disk — and lets <see cref="WarpPainter"/> draw. Moves glide via <see cref="HyperbolicMotion"/>.
/// </summary>
internal sealed class HyperbolicMapView : SpinningMapView
{
    private static readonly SKColor DiskFloor = SKColor.Parse("#1C232E");
    private static readonly SKColor Seam = SKColor.Parse("#0C1017");
    private static readonly SKColor Rim = SKColor.Parse("#6EA8FF").WithAlpha(110);
    private const string Caption = "your desktops on the {4,5} tiling of ℍ² · Poincaré disk";

    // The disk's radius as a fraction of the shorter viewport side.
    private const double DiskFill = 0.47;

    private static readonly IReadOnlyList<Mobius> BackgroundTiles = HyperbolicTiling.Tiles();

    private readonly HyperbolicMotion _motion = new();
    private bool _snap = true;
    private DesktopId? _anchor;

    public override string Name => "hyperbolic";
    public override void Snap() => _snap = true;
    public override bool Animating => _motion.Moving;
    public override void Step(double seconds) => _motion.Step(seconds);

    public override Control Render(MapViewRequest r)
    {
        _anchor = r.Anchor;
        IReadOnlyDictionary<DesktopId, Mobius> layout = r.Anchor is { } a
            ? HyperbolicTiling.Layout(r.Scene, a) : new Dictionary<DesktopId, Mobius>();
        if (_snap) { _motion.Snap(layout, SpinTarget); _snap = false; }
        else _motion.Retarget(layout, r.Anchor, SpinTarget);
        return Paint(r, _motion);
    }

    public override Control RenderStill(MapViewRequest r)
    {
        var still = new HyperbolicMotion();
        if (StillAnchor(r.Scene, r.Anchor ?? _anchor) is { } a) still.Snap(HyperbolicTiling.Layout(r.Scene, a), SpinTarget);
        return Paint(r, still);
    }

    private static Control Paint(MapViewRequest r, HyperbolicMotion motion)
    {
        double radius = DiskFill * Math.Min(r.Width, r.Height);
        double cx = r.Width / 2, cy = r.Height / 2;
        Point ToScreen(Complex z) => new(cx + radius * z.Real, cy + radius * z.Imaginary);
        SKPoint ToSk(Complex z) { Point p = ToScreen(z); return new SKPoint((float)p.X, (float)p.Y); }

        Mobius spin = Mobius.Rotate(motion.Spin);
        double h = HyperbolicTiling.KleinHalfSide;

        var tiles = new List<WarpTile>();
        foreach (SpatialRoom room in r.Scene.Rooms)
        {
            Mobius iso = spin.Then(motion.Room(room.Id));
            if (iso.Centre.Magnitude > 0.985) continue;
            tiles.Add(new WarpTile(room, (s, t) =>
                ToScreen(iso.Apply(HyperbolicTiling.KleinToDisk(new Complex(-h + 2 * h * s, -h + 2 * h * t))))));
        }

        // The base tile is a straight-sided square in the Klein model, so its edges walked there and carried
        // to the disk are the tiling's true geodesic seams.
        var seams = new SKPath();
        Mobius bg = spin.Then(motion.Background);
        Complex[] corners = { new(-h, -h), new(h, -h), new(h, h), new(-h, h) };
        foreach (Mobius tile in BackgroundTiles)
        {
            Mobius iso = bg.Then(tile);
            if (iso.Centre.Magnitude > 0.995) continue;
            const int PerEdge = 6;
            for (int e = 0; e < 4; e++)
                for (int i = 0; i < PerEdge; i++)
                {
                    Complex k = corners[e] + (corners[(e + 1) % 4] - corners[e]) * ((double)i / PerEdge);
                    SKPoint p = ToSk(iso.Apply(HyperbolicTiling.KleinToDisk(k)));
                    if (e == 0 && i == 0) seams.MoveTo(p); else seams.LineTo(p);
                }
            seams.Close();
        }

        var disk = new SKPath();
        disk.AddCircle((float)cx, (float)cy, (float)radius);

        return WarpPainter.Render(r, tiles,
            under: new[] { new WarpLayer(disk, DiskFloor) },
            over: new[] { new WarpLayer(seams, Seam, 1.3f), new WarpLayer(disk, Rim, 1.5f) },
            Caption);
    }
}
