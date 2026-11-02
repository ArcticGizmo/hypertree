using System.IO;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Hypertree.Desktops;
using Hypertree.Settings;
using Hypertree.Spatial;
using SkiaSharp;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// Draws the spatial map on the {4,5} tiling of the hyperbolic plane in the Poincaré disk (see
/// <see cref="HyperbolicTiling"/>). Each room is its usual glyph rendered once to a square texture, then
/// warped onto its tile through a mesh — exact on the tile's curved edges — so neighbouring rooms tessellate
/// with no gaps and every label and window turns with its tile. Avalonia's drawing API is affine-only, so the
/// warp goes through Skia directly via a custom draw operation.
/// </summary>
internal static class PoincareDiskPainter
{
    private static readonly Color FloorBase = Color.Parse("#1A212C");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // The disk's radius as a fraction of the shorter viewport side.
    private const double DiskFill = 0.47;

    // Rooms whose tile centre is this close to the rim are sub-pixel slivers; skip them.
    private const double MaxRoomRadius = 0.985;

    // The texture each room glyph is rendered to, in pixels. About the size the centre tile spans on a
    // typical display, so the room you're on stays crisp.
    private const int TextureSize = 288;

    private const int MeshCells = 12;

    private static readonly IReadOnlyList<Mobius> BackgroundTiles = HyperbolicTiling.Tiles();

    // Rendered room textures, keyed by everything that changes the glyph. Rebuilding one means a full
    // offscreen render, so a glide reuses them frame to frame. Old entries are dropped rather than disposed:
    // a frame already handed to the render thread may still be drawing them.
    private static readonly Dictionary<string, SKImage> Textures = new();

    public static Control Render(SpatialScene scene, double screenW, double screenH, HyperbolicMotion motion,
                                 Action<DesktopId>? onClick = null, Action<DesktopId>? onActivate = null,
                                 Guid? selectedGroup = null, MapStyle style = MapStyle.Board)
    {
        double radius = DiskFill * Math.Min(screenW, screenH);
        var centre = new Point(screenW / 2, screenH / 2);
        Mobius spin = Mobius.Rotate(motion.Spin);

        var activeGroups = new HashSet<Guid>();
        foreach (SpatialRoom r in scene.Rooms)
            if (r.Selected || r.Here) activeGroups.Add(r.GroupId);
        if (selectedGroup is { } sg) activeGroups.Add(sg);
        var overlapping = scene.Rooms.GroupBy(r => r.Pos).Where(g => g.Count() > 1).SelectMany(g => g).Select(r => r.Id).ToHashSet();
        var colours = scene.Groups.ToDictionary(g => g.Id, g => Color.Parse(g.Color));

        SKPoint ToScreen(Complex z) => new((float)(centre.X + radius * z.Real), (float)(centre.Y + radius * z.Imaginary));

        var rooms = new List<DiskRoom>();
        foreach (SpatialRoom room in scene.Rooms)
        {
            Mobius iso = spin.Then(motion.Room(room.Id));
            if (iso.Centre.Magnitude > MaxRoomRadius) continue;
            Color colour = colours.GetValueOrDefault(room.GroupId, Palette.Accent);
            SKImage tex = Texture(room, colour, style, overlapping.Contains(room.Id));
            double opacity = SpatialPainter.RoomOpacity(room, activeGroups.Contains(room.GroupId));
            rooms.Add(new DiskRoom(room.Id, iso.Inverse, tex, Mesh(iso, ToScreen), (byte)Math.Round(255 * opacity)));
        }

        var outlines = new SKPath();
        Mobius bg = spin.Then(motion.Background);
        foreach (Mobius tile in BackgroundTiles)
        {
            Mobius iso = bg.Then(tile);
            if (iso.Centre.Magnitude > 0.995) continue;
            TraceTile(outlines, iso, ToScreen);
        }

        var disk = new DiskView(new DiskFrame(centre, radius, rooms, outlines), onClick, onActivate)
        {
            Width = screenW, Height = screenH,
        };

        var caption = new TextBlock
        {
            Text = "your desktops on the {4,5} tiling of ℍ² · Poincaré disk",
            FontFamily = Mono, FontSize = 11.5, Foreground = Palette.MutedBrush, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 22),
        };
        return new Panel { Width = screenW, Height = screenH, Children = { disk, caption } };
    }

    // The base tile is a Euclidean square in the Klein model, so a regular grid over it, carried to the disk
    // and through the room's isometry, is a mesh whose outer edge follows the tile's true geodesic sides.
    private static SKVertices Mesh(Mobius iso, Func<Complex, SKPoint> toScreen)
    {
        int n = MeshCells, row = n + 1;
        double h = HyperbolicTiling.KleinHalfSide;
        var pos = new SKPoint[row * row];
        var tex = new SKPoint[row * row];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                var k = new Complex(-h + 2 * h * i / n, -h + 2 * h * j / n);
                pos[j * row + i] = toScreen(iso.Apply(HyperbolicTiling.KleinToDisk(k)));
                tex[j * row + i] = new SKPoint((float)(TextureSize * (double)i / n), (float)(TextureSize * (double)j / n));
            }

        var idx = new ushort[n * n * 6];
        int at = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                ushort a = (ushort)(j * row + i), b = (ushort)(a + 1), c = (ushort)(a + row), d = (ushort)(c + 1);
                idx[at++] = a; idx[at++] = b; idx[at++] = d;
                idx[at++] = a; idx[at++] = d; idx[at++] = c;
            }
        return SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, null, idx);
    }

    private static void TraceTile(SKPath path, Mobius iso, Func<Complex, SKPoint> toScreen)
    {
        const int PerEdge = 6;
        double h = HyperbolicTiling.KleinHalfSide;
        Complex[] corners = { new(-h, -h), new(h, -h), new(h, h), new(-h, h) };
        for (int e = 0; e < 4; e++)
        {
            Complex a = corners[e], b = corners[(e + 1) % 4];
            for (int i = 0; i < PerEdge; i++)
            {
                SKPoint p = toScreen(iso.Apply(HyperbolicTiling.KleinToDisk(a + (b - a) * ((double)i / PerEdge))));
                if (e == 0 && i == 0) path.MoveTo(p);
                else path.LineTo(p);
            }
        }
        path.Close();
    }

    // One room's square texture: a floor tinted to its group with the room on it. The board style gets a
    // square screen that fills the tile, like a window; metro and ASCII keep their own glyph, centred.
    // Rendered offscreen through Avalonia so the glyphs come along unchanged.
    private static SKImage Texture(SpatialRoom room, Color colour, MapStyle style, bool overlap)
    {
        string key = $"{room.Label}|{room.Selected}|{room.Here}|{room.WindowCount}|{room.IsMainGroup}|{colour}|{style}|{overlap}";
        if (Textures.TryGetValue(key, out SKImage? cached)) return cached;
        if (Textures.Count > 256) Textures.Clear();

        Control glyph;
        if (style == MapStyle.Board)
        {
            glyph = SquareScreen(room, colour);
        }
        else
        {
            double k = TextureSize * 0.92 / 140;
            glyph = SpatialPainter.RoomHost(room, colour, new Size(140 * k, 72 * k), k, style, overlap, null, null);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
        }
        var floor = new Grid
        {
            Width = TextureSize, Height = TextureSize,
            Background = new SolidColorBrush(ScenePaint.Lerp(FloorBase, colour, room.IsMainGroup ? 0.1 : 0.28)),
            Children = { glyph },
        };
        floor.Measure(new Size(TextureSize, TextureSize));
        floor.Arrange(new Rect(0, 0, TextureSize, TextureSize));

        using var rtb = new RenderTargetBitmap(new PixelSize(TextureSize, TextureSize), new Avalonia.Vector(96, 96));
        rtb.Render(floor);
        using var png = new MemoryStream();
        rtb.Save(png);
        SKImage image = SKImage.FromEncodedData(png.ToArray());
        Textures[key] = image;
        return image;
    }

    // The board tile reshaped to the square: a screen of mock windows over a caption bar, inset from the tile
    // edge so a sliver of group-tinted floor marks the seams.
    private static Control SquareScreen(SpatialRoom room, Color colour)
    {
        const double S = TextureSize, Inset = 12, CapH = 64;
        double w = S - 2 * Inset, screenH = w - CapH;
        Color border = room.Selected ? Palette.Accent : room.Here ? Palette.Here : colour;
        double bt = room.Selected || room.Here ? 6 : 3;

        var screen = new Canvas { Width = w, Height = screenH };
        if (room.WindowCount > 0)
        {
            Color win = ScenePaint.Lerp(colour, SpatialPainter.WinBase, 0.45);
            AddWin(screen, 22, 22, 128, 46, win, 1.0);
            AddWin(screen, 22, 80, 92, 40, win, 1.0);
            AddWin(screen, w - 22 - 70, 34, 70, 96, win, 0.7);
            AddWin(screen, 120, 132, 64, 30, win, 0.5);
        }
        var badge = new Border
        {
            Height = 36, MinWidth = 36, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x14, 0x1D)),
            Child = new TextBlock
            {
                Text = room.WindowCount.ToString(), FontFamily = Mono, FontSize = 20, FontWeight = FontWeight.SemiBold,
                Foreground = Palette.InkBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, 14);
        Canvas.SetTop(badge, screenH - 36 - 14);
        screen.Children.Add(badge);

        var caption = new Border
        {
            Height = CapH, Background = new SolidColorBrush(SpatialPainter.CapBg),
            Child = new TextBlock
            {
                Text = room.Label, FontFamily = Mono, FontSize = 26, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = room.Selected ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = room.Selected ? Palette.InkBrush : Palette.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
            },
        };
        Grid.SetRow(caption, 1);

        var body = new Grid { RowDefinitions = new RowDefinitions($"*,{CapH}"), Children = { screen, caption } };
        if (room.Here)
            body.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(10),
                Background = new SolidColorBrush(Palette.Here), BorderBrush = new SolidColorBrush(SpatialPainter.CapBg),
                BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            });

        return new Border
        {
            Width = w, Height = w, Margin = new Thickness(Inset), ClipToBounds = true,
            CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(SpatialPainter.TileBg),
            BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(bt), Child = body,
        };
    }

    private static void AddWin(Canvas c, double x, double y, double w, double h, Color color, double opacity)
    {
        var r = new Avalonia.Controls.Shapes.Rectangle { Width = w, Height = h, RadiusX = 6, RadiusY = 6, Fill = new SolidColorBrush(color), Opacity = opacity };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    /// <summary>A room as drawn this frame: its texture, the mesh warping it onto its tile, its brightness,
    /// and the inverse isometry that takes a disk point back into the base tile for hit-testing.</summary>
    private sealed record DiskRoom(DesktopId Id, Mobius ToBase, SKImage Texture, SKVertices Mesh, byte Alpha);

    /// <summary>Everything one frame draws, built on the UI thread and only read by the render thread.</summary>
    private sealed record DiskFrame(Point Centre, double Radius, IReadOnlyList<DiskRoom> Rooms, SKPath Outlines);

    private sealed class DiskView : Control
    {
        private readonly DiskFrame _frame;
        private readonly Action<DesktopId>? _onClick, _onActivate;

        public DiskView(DiskFrame frame, Action<DesktopId>? onClick, Action<DesktopId>? onActivate)
        {
            _frame = frame;
            _onClick = onClick;
            _onActivate = onActivate;
            PointerPressed += OnPressed;
        }

        public override void Render(DrawingContext context) => context.Custom(new DiskDraw(new Rect(Bounds.Size), _frame));

        // The tiles tessellate, so at most one contains the point: carry it back to the base tile through each
        // room's inverse and test there.
        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            Point at = e.GetPosition(this);
            var z = new Complex((at.X - _frame.Centre.X) / _frame.Radius, (at.Y - _frame.Centre.Y) / _frame.Radius);
            if (z.Magnitude >= 1) return;
            foreach (DiskRoom r in _frame.Rooms)
            {
                if (!HyperbolicTiling.InBaseTile(r.ToBase.Apply(z))) continue;
                if (e.ClickCount >= 2) _onActivate?.Invoke(r.Id);
                else _onClick?.Invoke(r.Id);
                e.Handled = true;
                return;
            }
        }
    }

    private sealed class DiskDraw(Rect bounds, DiskFrame frame) : ICustomDrawOperation
    {
        private static readonly SKColor DiskFloor = SKColor.Parse("#1C232E");
        private static readonly SKColor Seam = SKColor.Parse("#0C1017");
        private static readonly SKColor Rim = SKColor.Parse("#6EA8FF");

        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;

            float cx = (float)frame.Centre.X, cy = (float)frame.Centre.Y, r = (float)frame.Radius;
            using (var floor = new SKPaint { IsAntialias = true, Color = DiskFloor })
                canvas.DrawCircle(cx, cy, r, floor);

            foreach (DiskRoom room in frame.Rooms)
            {
                using SKShader shader = room.Texture.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                              new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var paint = new SKPaint { IsAntialias = true, Shader = shader, Color = new SKColor(255, 255, 255, room.Alpha) };
                canvas.DrawVertices(room.Mesh, SKBlendMode.Modulate, paint);
            }

            using (var seams = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = Seam })
                canvas.DrawPath(frame.Outlines, seams);

            using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = Rim.WithAlpha(110) })
                canvas.DrawCircle(cx, cy, r, rim);
        }
    }
}
