using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Hypertree.Spatial;
using SkiaSharp;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The spatial map as a Droste vortex (see <see cref="VortexMap"/>): rows are rings shrinking into the centre
/// forever, columns are spokes. The anchor sits at the bottom of the middle ring. Stepping along a row spins
/// the vortex and stepping between rows zooms one ring in or out — the whole picture moves as one, since
/// <c>exp</c> turns every grid translation into a single rotate-and-scale. Geometry only; drawn by
/// <see cref="WarpPainter"/>.
/// </summary>
internal sealed class VortexMapView : CentredWarpView
{
    private static readonly SKColor Floor = SKColor.Parse("#161C26");
    private static readonly SKColor Seam = SKColor.Parse("#0C1017");
    private static readonly SKColor Eye = SKColor.Parse("#6EA8FF");
    private const string Caption = "your desktops through exp(z) · a Droste vortex — it's rooms all the way down";

    // The anchor ring's radius as a fraction of the shorter viewport side.
    private const double RingFill = 0.3;

    public override string Name => "vortex";

    protected override Control Paint(MapViewRequest r, GridPose at, bool still)
    {
        int spokes = VortexMap.Spokes(r.Scene);
        double ring = RingFill * Math.Min(r.Width, r.Height);
        double cx = r.Width / 2, cy = r.Height / 2;
        Point ToScreen(Complex z) => new(cx + ring * z.Real, cy + ring * z.Imaginary);
        Point Map(double u, double v) => ToScreen(VortexMap.Map(u, v, at.U, at.V, spokes, at.Spin));

        var tiles = r.Scene.Rooms
            .Select(room => new WarpTile(room, (s, t) => Map(room.Pos.X - 0.5 + s, room.Pos.Y - 0.5 + t)))
            .ToList();

        // Seams: a circle at every row boundary from a pixel across out past the corners, and a spoke at every
        // column boundary from the eye outward.
        var seams = new SKPath();
        double far = Math.Sqrt(cx * cx + cy * cy) / ring;
        double rowOffset = at.V - Math.Floor(at.V) - 0.5;
        for (int k = -200; k <= 200; k++)
        {
            double radius = VortexMap.RingRadius(k - rowOffset, spokes);
            if (radius * ring < 1.5) continue;
            if (radius > far) break;
            seams.AddCircle((float)cx, (float)cy, (float)(radius * ring));
        }
        for (int k = 0; k < spokes; k++)
        {
            Complex dir = VortexMap.Map(Math.Floor(at.U) + k + 0.5, at.V, at.U, at.V, spokes, at.Spin);
            dir /= dir.Magnitude;
            seams.MoveTo((float)cx, (float)cy);
            seams.LineTo((float)(cx + dir.Real * far * ring), (float)(cy + dir.Imaginary * far * ring));
        }

        var backdrop = new SKPath();
        backdrop.AddRect(SKRect.Create(0, 0, (float)r.Width, (float)r.Height));
        var eye = new SKPath();
        eye.AddCircle((float)cx, (float)cy, 4);

        return WarpPainter.Render(r, tiles,
            under: new[] { new WarpLayer(backdrop, Floor.WithAlpha(150)) },
            over: new[] { new WarpLayer(seams, Seam, 1.2f), new WarpLayer(eye, Eye) },
            Caption);
    }
}
