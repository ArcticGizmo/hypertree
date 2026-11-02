using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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

/// <summary>A room for <see cref="WarpPainter"/>: where each point of its square lands on screen, as
/// <c>(s, t) ∈ [0,1]²</c> → pixels, with <c>s</c> across and <c>t</c> down the room's own frame.</summary>
internal sealed record WarpTile(SpatialRoom Room, Func<double, double, Point> Map);

/// <summary>A path the view draws under or over the rooms — a disk, seams, a rim. A stroke width of zero fills.</summary>
internal sealed record WarpLayer(SKPath Path, SKColor Color, float Stroke = 0);

/// <summary>
/// The engine behind every curved view. A view supplies only geometry — a map from each room's square to
/// the screen, plus background paths — and this does the rest: each room's glyph is rendered once to a
/// square texture (text-free, so it can turn freely), warped onto its tile through a mesh that follows the
/// map exactly, so tiles meet edge to edge whatever the map; then each room's label is drawn upright in
/// screen space over its tile, so it reads the right way round however the tile is turned. Clicks are
/// resolved against each tile's warped outline. Avalonia's drawing API is affine-only, so the warp goes
/// through Skia directly in a custom draw operation.
/// </summary>
internal static class WarpPainter
{
    private static readonly Color FloorBase = Color.Parse("#1A212C");
    private static readonly SKTypeface LabelFace =
        SKTypeface.FromFamilyName("Cascadia Code") ?? SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;

    // The texture each room glyph is rendered to, in pixels — about the size the biggest tile spans on a
    // typical display, so the room you're on stays crisp.
    private const int TextureSize = 288;

    private const int MeshCells = 12;

    // Below this on-screen size a tile is too small for a legible label, so it goes without.
    private const double MinLabelTile = 46;

    // Rendered room textures, keyed by everything that changes the glyph. Rebuilding one means a full
    // offscreen render, so an animation reuses them frame to frame. Old entries are dropped rather than
    // disposed: a frame already handed to the render thread may still be drawing them.
    private static readonly Dictionary<string, SKImage> Textures = new();

    public static Control Render(MapViewRequest r, IReadOnlyList<WarpTile> tiles,
                                 IReadOnlyList<WarpLayer> under, IReadOnlyList<WarpLayer> over, string caption)
    {
        SpatialScene scene = r.Scene;
        var activeGroups = new HashSet<Guid>();
        foreach (SpatialRoom room in scene.Rooms)
            if (room.Selected || room.Here) activeGroups.Add(room.GroupId);
        if (r.SelectedGroup is { } sg) activeGroups.Add(sg);
        var colours = scene.Groups.ToDictionary(g => g.Id, g => Color.Parse(g.Color));

        var rooms = new List<WarpRoom>(tiles.Count);
        foreach (WarpTile tile in tiles)
        {
            SpatialRoom room = tile.Room;
            Point[] outline = Outline(tile.Map);
            if (!OnScreen(outline, r.Width, r.Height)) continue;

            Color colour = colours.GetValueOrDefault(room.GroupId, Palette.Accent);
            double opacity = SpatialPainter.RoomOpacity(room, activeGroups.Contains(room.GroupId));
            if (r.Spotlight is { } spot) opacity = spot.Contains(room.Id) ? 1.0 : 0.1;

            rooms.Add(new WarpRoom(room.Id, outline, Texture(room, colour, r.Style), Mesh(tile.Map),
                                   (byte)Math.Round(255 * opacity), Label(room, tile.Map, opacity)));
        }

        var view = new WarpView(new WarpFrame(under, rooms, over), r.OnClick, r.OnActivate) { Width = r.Width, Height = r.Height };
        var text = new TextBlock
        {
            Text = caption, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 11.5,
            Foreground = Palette.MutedBrush, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 22),
        };
        return new Panel { Width = r.Width, Height = r.Height, Children = { view, text } };
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static SKVertices Mesh(Func<double, double, Point> map)
    {
        int n = MeshCells, row = n + 1;
        var pos = new SKPoint[row * row];
        var tex = new SKPoint[row * row];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double s = (double)i / n, t = (double)j / n;
                Point p = map(s, t);
                pos[j * row + i] = new SKPoint((float)p.X, (float)p.Y);
                tex[j * row + i] = new SKPoint((float)(TextureSize * s), (float)(TextureSize * t));
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

    // The tile's warped edge, walked round its four sides — what a click is tested against.
    private static Point[] Outline(Func<double, double, Point> map)
    {
        const int PerSide = 8;
        var pts = new Point[4 * PerSide];
        for (int i = 0; i < PerSide; i++)
        {
            double f = (double)i / PerSide;
            pts[i] = map(f, 0);
            pts[PerSide + i] = map(1, f);
            pts[2 * PerSide + i] = map(1 - f, 1);
            pts[3 * PerSide + i] = map(0, 1 - f);
        }
        return pts;
    }

    private static bool OnScreen(Point[] outline, double w, double h)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (Point p in outline)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return maxX > 0 && maxY > 0 && minX < w && minY < h && (maxX - minX) + (maxY - minY) > 3;
    }

    private static bool Contains(Point[] poly, Point p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    // ── Upright labels ───────────────────────────────────────────────────────

    // The label sits in the lower part of the tile, but "lower" in screen space — so however the tile is
    // turned, the text runs left to right the right way up. Sized from the tile's on-screen extent.
    private static WarpLabel? Label(SpatialRoom room, Func<double, double, Point> map, double opacity)
    {
        Point c = map(0.5, 0.5);
        double across = Dist(map(0, 0.5), map(1, 0.5)), down = Dist(map(0.5, 0), map(0.5, 1));
        double size = (across + down) / 2;
        if (size < MinLabelTile) return null;

        float fontSize = (float)Math.Clamp(size * 0.085, 9, 22);
        string text = room.Label;
        using var font = new SKFont(LabelFace, fontSize);
        float maxWidth = (float)(size * 0.78);
        string count = room.WindowCount.ToString();
        float countWidth = font.MeasureText(count) + fontSize * 0.9f;
        while (text.Length > 1 && font.MeasureText(text) + countWidth > maxWidth) text = text[..^1];
        if (text != room.Label) text = text.Length > 1 ? text[..^1] + "…" : text;

        float textWidth = font.MeasureText(text);
        float padX = fontSize * 0.7f, h = fontSize * 1.75f, dot = room.Here ? fontSize * 0.75f : 0;
        float width = padX * 2 + dot + textWidth + countWidth;
        var centre = new SKPoint((float)c.X, (float)(c.Y + size * 0.26));
        return new WarpLabel(text, count, centre, width, h, fontSize, dot, room.Selected, room.Here, (byte)Math.Round(255 * opacity));
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ── Textures ─────────────────────────────────────────────────────────────

    // One room's square texture, text-free so it can turn with the tile: a floor tinted to its group with the
    // room drawn on it in the app's Map style. Rendered offscreen through Avalonia.
    private static SKImage Texture(SpatialRoom room, Color colour, MapStyle style)
    {
        string key = $"{room.Selected}|{room.Here}|{Math.Min(room.WindowCount, 9)}|{room.IsMainGroup}|{colour}|{style}";
        if (Textures.TryGetValue(key, out SKImage? cached)) return cached;
        if (Textures.Count > 256) Textures.Clear();

        Control glyph = style switch
        {
            MapStyle.Metro => MetroGlyph(room, colour),
            MapStyle.Ascii => AsciiGlyph(room, colour),
            _ => BoardGlyph(room, colour),
        };
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

    private static Color StateColour(SpatialRoom room, Color colour) =>
        room.Selected ? Palette.Accent : room.Here ? Palette.Here : colour;

    // The board tile as a square screen of mock windows, inset so a sliver of group floor marks the seams.
    private static Control BoardGlyph(SpatialRoom room, Color colour)
    {
        const double Inset = 12;
        double w = TextureSize - 2 * Inset;
        var screen = new Canvas { Width = w, Height = w };
        int n = room.WindowCount;
        if (n > 0)
        {
            Color win = ScenePaint.Lerp(colour, SpatialPainter.WinBase, 0.45);
            AddRect(screen, 22, 22, 128, 50, win, 1.0, 6);
            if (n > 1) AddRect(screen, 22, 84, 92, 44, win, 1.0, 6);
            if (n > 2) AddRect(screen, w - 22 - 74, 34, 74, 100, win, 0.7, 6);
            if (n > 3) AddRect(screen, 124, 140, 66, 34, win, 0.5, 6);
        }
        bool marked = room.Selected || room.Here;
        return new Border
        {
            Width = w, Height = w, Margin = new Thickness(Inset), ClipToBounds = true,
            CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(SpatialPainter.TileBg),
            BorderBrush = new SolidColorBrush(StateColour(room, colour)), BorderThickness = new Thickness(marked ? 6 : 3),
            Child = screen,
        };
    }

    // A metro station: a donut in the group colour, ringed in focus blue when selected.
    private static Control MetroGlyph(SpatialRoom room, Color colour)
    {
        var canvas = new Canvas { Width = TextureSize, Height = TextureSize };
        double cx = TextureSize / 2.0, cy = TextureSize * 0.4, r = 46;
        if (room.Here) AddDisc(canvas, cx, cy, r * 1.8, Palette.Here, 0.2);
        canvas.Children.Add(Placed(new Ellipse
        {
            Width = 2 * r, Height = 2 * r, Fill = new SolidColorBrush(Color.Parse("#0F131B")),
            Stroke = new SolidColorBrush(StateColour(room, colour)), StrokeThickness = 16,
        }, cx - r, cy - r));
        if (room.Selected)
        {
            double fr = r + 22;
            canvas.Children.Add(Placed(new Ellipse
            {
                Width = 2 * fr, Height = 2 * fr, Stroke = new SolidColorBrush(Palette.Accent), StrokeThickness = 7,
            }, cx - fr, cy - fr));
        }
        // A stub of line through the station, along the room's row.
        canvas.Children.Insert(0, Placed(new Rectangle
        {
            Width = TextureSize, Height = 18, Fill = new SolidColorBrush(colour, 0.55),
        }, 0, cy - 9));
        return canvas;
    }

    // An ASCII card: a box-drawn frame with a # per window, double-ruled when selected. Characters only as
    // ornament — the label is drawn upright separately.
    private static Control AsciiGlyph(SpatialRoom room, Color colour)
    {
        (char tl, char tr, char bl, char br, char h, char v) = room.Selected
            ? ('╔', '╗', '╚', '╝', '═', '║') : ('┌', '┐', '└', '┘', '─', '│');
        const int Inner = 8;
        string wins = new string('#', Math.Min(room.WindowCount, Inner - 2)).PadRight(Inner - 2);
        string body = $"{tl}{new string(h, Inner)}{tr}\n{v} {wins} {v}\n{v}{new string(' ', Inner)}{v}\n{bl}{new string(h, Inner)}{br}";
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0C0F16")),
            Child = new TextBlock
            {
                Text = body, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 40,
                LineHeight = 52, Foreground = new SolidColorBrush(StateColour(room, colour)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 22, 0, 0),
            },
        };
    }

    private static void AddRect(Canvas c, double x, double y, double w, double h, Color color, double opacity, double radius) =>
        c.Children.Add(Placed(new Rectangle
        {
            Width = w, Height = h, RadiusX = radius, RadiusY = radius, Fill = new SolidColorBrush(color), Opacity = opacity,
        }, x, y));

    private static void AddDisc(Canvas c, double cx, double cy, double r, Color color, double opacity) =>
        c.Children.Add(Placed(new Ellipse { Width = 2 * r, Height = 2 * r, Fill = new SolidColorBrush(color, opacity) }, cx - r, cy - r));

    private static Control Placed(Control c, double x, double y)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        return c;
    }

    // ── Frame & drawing ──────────────────────────────────────────────────────

    private sealed record WarpLabel(string Text, string Count, SKPoint Centre, float Width, float Height,
                                    float FontSize, float Dot, bool Selected, bool Here, byte Alpha);

    private sealed record WarpRoom(DesktopId Id, Point[] Outline, SKImage Texture, SKVertices Mesh, byte Alpha, WarpLabel? Label);

    /// <summary>Everything one frame draws, built on the UI thread and only read by the render thread.</summary>
    private sealed record WarpFrame(IReadOnlyList<WarpLayer> Under, IReadOnlyList<WarpRoom> Rooms, IReadOnlyList<WarpLayer> Over);

    private sealed class WarpView : Control
    {
        private readonly WarpFrame _frame;
        private readonly Action<DesktopId>? _onClick, _onActivate;

        public WarpView(WarpFrame frame, Action<DesktopId>? onClick, Action<DesktopId>? onActivate)
        {
            _frame = frame;
            _onClick = onClick;
            _onActivate = onActivate;
            if (onClick is not null || onActivate is not null) PointerPressed += OnPressed;
        }

        public override void Render(DrawingContext context) => context.Custom(new WarpDraw(new Rect(Bounds.Size), _frame));

        // Tiles tessellate, so at most one outline contains the point.
        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            Point at = e.GetPosition(this);
            foreach (WarpRoom r in _frame.Rooms)
            {
                if (!Contains(r.Outline, at)) continue;
                if (e.ClickCount >= 2) _onActivate?.Invoke(r.Id);
                else _onClick?.Invoke(r.Id);
                e.Handled = true;
                return;
            }
        }
    }

    private sealed class WarpDraw(Rect bounds, WarpFrame frame) : ICustomDrawOperation
    {
        private static readonly SKColor ChipBg = SKColor.Parse("#161C27"), ChipInk = SKColor.Parse("#0A0D12");
        private static readonly SKColor Ink = SKColor.Parse("#E6EDF7"), Muted = SKColor.Parse("#9AA6B8");
        private static readonly SKColor Accent = ToSk(Palette.Accent), Here = ToSk(Palette.Here);

        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;

            foreach (WarpLayer layer in frame.Under) DrawLayer(canvas, layer);

            foreach (WarpRoom room in frame.Rooms)
            {
                using SKShader shader = room.Texture.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                              new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var paint = new SKPaint { IsAntialias = true, Shader = shader, Color = new SKColor(255, 255, 255, room.Alpha) };
                canvas.DrawVertices(room.Mesh, SKBlendMode.Modulate, paint);
            }

            foreach (WarpLayer layer in frame.Over) DrawLayer(canvas, layer);

            foreach (WarpRoom room in frame.Rooms)
                if (room.Label is { } label) DrawLabel(canvas, label);
        }

        private static void DrawLayer(SKCanvas canvas, WarpLayer layer)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Color = layer.Color,
                Style = layer.Stroke > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill, StrokeWidth = layer.Stroke,
            };
            canvas.DrawPath(layer.Path, paint);
        }

        // A pill: here-dot, label, then the window count dimmer — accent-filled when selected, like the
        // metro chips.
        private static void DrawLabel(SKCanvas canvas, WarpLabel l)
        {
            var rect = SKRect.Create(l.Centre.X - l.Width / 2, l.Centre.Y - l.Height / 2, l.Width, l.Height);
            using (var bg = new SKPaint { IsAntialias = true, Color = (l.Selected ? Accent : ChipBg).WithAlpha((byte)(l.Alpha * (l.Selected ? 1 : 0.88))) })
                canvas.DrawRoundRect(rect, l.Height / 2, l.Height / 2, bg);

            using var font = new SKFont(LabelFace, l.FontSize) { Embolden = l.Selected };
            float x = rect.Left + l.FontSize * 0.7f, baseline = l.Centre.Y + l.FontSize * 0.35f;
            if (l.Here)
            {
                using var dot = new SKPaint { IsAntialias = true, Color = Here.WithAlpha(l.Alpha) };
                canvas.DrawCircle(x + l.Dot * 0.35f, l.Centre.Y, l.Dot * 0.35f, dot);
                x += l.Dot;
            }
            using (var ink = new SKPaint { IsAntialias = true, Color = (l.Selected ? ChipInk : Ink).WithAlpha(l.Alpha) })
                canvas.DrawText(l.Text, x, baseline, SKTextAlign.Left, font, ink);
            x += font.MeasureText(l.Text) + l.FontSize * 0.5f;
            using var dim = new SKPaint { IsAntialias = true, Color = (l.Selected ? ChipInk.WithAlpha(150) : Muted).WithAlpha((byte)(l.Alpha * 0.8)) };
            using var small = new SKFont(LabelFace, l.FontSize * 0.85f);
            canvas.DrawText(l.Count, x, baseline, SKTextAlign.Left, small, dim);
        }

        private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
    }
}
