using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// Draws the <b>spatial map</b>: desktops as freely-placed room tiles (the board tile look, tinted to their
/// group's colour) sitting inside translucent group hulls — each the rounded "tetris" outline of one
/// edge-connected clump of the group's cells, so a scattered group reads as broken. It only draws —
/// <see cref="SpatialLayout"/> owns world placement (and the hull outlines) and the shared
/// <see cref="MapCamera"/> owns panning, so the spatial model frames and moves exactly like the rows.
///
/// M1 is read-only (captures, previews); selection/drag/tidy interaction lands in later milestones.
/// </summary>
internal static class SpatialPainter
{
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Color.Parse("#2A3444");
    private static readonly Color CapBg = Color.Parse("#161C27");
    private static readonly Color Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8");
    private static readonly Color Focus = Color.Parse("#6EA8FF"), Here = Color.Parse("#34D399");
    private static readonly Color WinBase = Color.Parse("#374357");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");
    // ASCII / Metro room styling, matching AsciiPainter and MetroPainter so a room reads the same in either model.
    private static readonly Color AsciiGround = Color.Parse("#0C0F16");
    private static readonly Color MetroBg = Color.Parse("#0F131B"), ChipBase = Color.Parse("#111722"), ChipInk = Color.Parse("#0A0D12");
    private static readonly FontFamily Sans = new("Inter,Segoe UI,sans-serif");
    // Larger than the row/list view's ASCII card: a spatial cell (140×72) is far bigger than a list row, so
    // the box grows to fill it instead of floating in wasted space.
    private const double AsciiFont = 18;
    private const int AsciiInnerW = 12;

    // Base geometry (unscaled). A room is the board tile, kept wide so long desktop names fit; the grid
    // stride leaves generous gaps so rooms and their hulls have room to breathe (tile is 140×72, so these
    // strides leave ~60px between neighbours).
    private const double BaseTileW = 140, BaseScrH = 50, BaseCapH = 22;
    private const double BaseStrideX = 202, BaseStrideY = 140, BaseHullPad = 16;
    private static double TileH => BaseScrH + BaseCapH; // 72

    /// <summary>The spatial metrics — a near-square grid, unlike the tall row pitch, since 2-D placement
    /// wants comparable breathing room on both axes.</summary>
    public static SceneMetrics Metrics(double s) => new(
        CellStride: BaseStrideX * s, CellWidth: BaseTileW * s, CellHeight: TileH * s,
        RowPitch: BaseStrideY * s, RowHeight: TileH * s);

    public static Control Render(SpatialScene scene, double screenW, double screenH, double s, MapCamera camera,
                                 Action<DesktopId>? onClick = null, Action<DesktopId>? onActivate = null,
                                 IList<(DesktopId Id, Rect Rect)>? hits = null, Guid? selectedGroup = null,
                                 MapStyle style = MapStyle.Board, IDictionary<DesktopId, Control>? roomHosts = null,
                                 Guid? hoverGroup = null)
    {
        var layout = new SpatialLayout(scene, Metrics(s));
        camera.Update(layout, screenW, screenH);
        double ox = camera.OffsetX, oy = camera.OffsetY;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // A group hull only lifts to its bright fill when it's "live": the blue selection sits in it, it's
        // where we currently are (the green "here"), the mouse is hovering it, or the whole group is picked up.
        // At rest every other group's fill is halved so the map isn't colour-washed by groups you're not in.
        var activeGroups = new HashSet<Guid>();
        foreach (PlacedRoom pr in layout.Rooms)
            if (pr.Room.Selected || pr.Room.Here) activeGroups.Add(pr.Room.GroupId);
        if (selectedGroup is { } sgId) activeGroups.Add(sgId);
        if (hoverGroup is { } hgId) activeGroups.Add(hgId);

        // Hulls first (behind the rooms), then their name badges, then the rooms on top. The hull is the
        // spatial stand-in for the row model's branch box / metro route, so it stays whatever the room style.
        foreach (GroupHull hull in layout.Hulls(BaseHullPad * s, BaseHullPad * s))
            PaintHull(canvas, hull, ox, oy, s,
                      active: activeGroups.Contains(hull.Group.Id),
                      selected: selectedGroup is { } sg && hull.Group.Id == sg);

        // Cells holding more than one room — flagged so a stack shows a warning instead of hiding silently.
        var overlapping = layout.Rooms.GroupBy(r => r.Room.Pos).Where(g => g.Count() > 1)
                                .SelectMany(g => g).Select(r => r.Room.Id).ToHashSet();

        foreach (PlacedRoom placed in layout.Rooms)
        {
            DesktopId id = placed.Room.Id;
            Color groupColor = Color.Parse(scene.Groups.First(g => g.Id == placed.Room.GroupId).Color);
            var cell = new Rect(placed.Rect.Left + ox, placed.Rect.Top + oy, placed.Rect.Width, placed.Rect.Height);

            // Each room lives in its own host canvas positioned at its cell, so a drag can move the host
            // without re-rendering the board (a re-render mid-drag would drop the pointer capture). The glyph
            // is drawn in the host's local coordinates.
            var host = new Canvas { Width = cell.Width, Height = cell.Height };
            var local = new Rect(0, 0, cell.Width, cell.Height);
            switch (style) // the room glyph follows the app's Map style, so List and Spatial read as one app
            {
                case MapStyle.Metro: DrawMetroRoom(host, placed.Room, groupColor, local, s); break;
                case MapStyle.Ascii: DrawAsciiRoom(host, placed.Room, groupColor, local, s); break;
                default: DrawBoardRoom(host, placed.Room, groupColor, local, s); break;
            }
            if (overlapping.Contains(id)) AddOverlapBadge(host, local, s);

            // A desktop dims unless its group is "live" (the selection, "here", hover, or a whole-group pick
            // sits in it). This is the de-emphasis 0-window rooms used to carry, now repurposed to mean "not
            // the group you're in" — so a room reads the same whether or not it holds windows.
            host.Opacity = activeGroups.Contains(placed.Room.GroupId) ? 1.0 : 0.5;

            if (onClick is not null || onActivate is not null)
            {
                var hit = new Border { Width = cell.Width, Height = cell.Height, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
                hit.PointerPressed += (_, e) => { if (e.ClickCount >= 2) onActivate?.Invoke(id); else onClick?.Invoke(id); };
                host.Children.Add(hit); // topmost within the host, so it catches the press for every style
            }

            Canvas.SetLeft(host, cell.X);
            Canvas.SetTop(host, cell.Y);
            canvas.Children.Add(host);
            if (roomHosts is not null) roomHosts[id] = host;
            hits?.Add((id, cell));
        }

        PaintOffscreenMarkers(canvas, layout, scene, ox, oy, screenW, screenH, s);
        return canvas;
    }

    // Rooms that fell off the edge of the viewport get an arrow on the border pointing their way: draw a
    // straight line from the viewport centre to the off-screen room, and where it crosses the border sits an
    // arrow (in that room's group colour) with a soft colour bleed behind it. The geometry is pure — see
    // OffscreenMarkers — so here we only turn each marker into pixels, on top of the map.
    private static void PaintOffscreenMarkers(Canvas canvas, SpatialLayout layout, SpatialScene scene,
                                              double ox, double oy, double screenW, double screenH, double s)
    {
        IReadOnlyList<EdgeMarker> markers = OffscreenMarkers.Compute(layout, ox, oy, screenW, screenH, 3 * s);
        if (markers.Count == 0) return;

        // room → its group colour, looked up once for the frame.
        var colourById = new Dictionary<DesktopId, Color>();
        foreach (PlacedRoom pr in layout.Rooms)
            colourById[pr.Room.Id] = Color.Parse(scene.Groups.First(g => g.Id == pr.Room.GroupId).Color);

        foreach (EdgeMarker m in markers)
            AddEdgeMarker(canvas, m.X, m.Y, m.Angle, colourById.TryGetValue(m.Room, out Color c) ? c : Focus, s);
    }

    // One border indicator: a radial colour bleed centred on the border point (clipped by the canvas, so it
    // reads as a gradient washing in from the edge) with a filled triangle whose tip touches the border and
    // points outward toward the off-screen room.
    private static void AddEdgeMarker(Canvas canvas, double x, double y, double angle, Color c, double s)
    {
        double glow = 130 * s;
        var halo = new Ellipse
        {
            Width = glow, Height = glow, IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(0xB0, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
                },
            },
        };
        Canvas.SetLeft(halo, x - glow / 2);
        Canvas.SetTop(halo, y - glow / 2);
        canvas.Children.Add(halo);

        // Triangle in absolute coordinates: tip on the border, base set back along the inward direction and
        // spread to either side (mirrors HullGeometry, which also builds an absolute-space Path).
        double len = 22 * s, half = 13 * s;
        double ca = Math.Cos(angle), sa = Math.Sin(angle);       // unit vector toward the room (outward)
        double bxp = x - ca * len, byp = y - sa * len;           // arrow base, set back from the border
        double perpX = -sa, perpY = ca;                          // perpendicular, to spread the base
        var tip = new Point(x, y);
        var b1 = new Point(bxp + perpX * half, byp + perpY * half);
        var b2 = new Point(bxp - perpX * half, byp - perpY * half);

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(tip, isFilled: true);
            ctx.LineTo(b1);
            ctx.LineTo(b2);
            ctx.EndFigure(true);
        }
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = geo, Fill = new SolidColorBrush(c), IsHitTestVisible = false,
            Stroke = new SolidColorBrush(CapBg), StrokeThickness = 1.5 * s,
        });
    }

    private static void DrawBoardRoom(Canvas host, SpatialRoom room, Color groupColor, Rect cell, double s)
    {
        Control tile = Tile(room, groupColor, s);
        Canvas.SetLeft(tile, cell.X);
        Canvas.SetTop(tile, cell.Y);
        host.Children.Add(tile);
    }

    // A small amber "!" badge at the room's top-right — the map's way of saying "another room is stacked on
    // this cell" now that a move never shoves things aside on its own.
    private static void AddOverlapBadge(Canvas host, Rect cell, double s)
    {
        var badge = new Border
        {
            Width = 17 * s, Height = 17 * s, CornerRadius = new CornerRadius(9 * s),
            Background = new SolidColorBrush(Color.Parse("#F59E0B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#0D0D11")), BorderThickness = new Thickness(1.5 * s),
            Child = new TextBlock
            {
                Text = "!", FontSize = 11 * s, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#1A1206")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, cell.Width - 20 * s);
        Canvas.SetTop(badge, -5 * s);
        host.Children.Add(badge);
    }

    /// <summary>The grid pitch at scale <paramref name="s"/> — what a drag converts pixel travel into whole
    /// cell steps against.</summary>
    public static (double X, double Y) Stride(double s)
    {
        SceneMetrics m = Metrics(s);
        return (m.CellStride, m.RowPitch);
    }

    private static void PaintHull(Canvas canvas, GroupHull hull, double ox, double oy, double s, bool active, bool selected)
    {
        Color c = Color.Parse(hull.Group.Color);
        bool main = hull.Group.IsMain; // the ungrouped bucket: barely-there, dashed, no deliberate grouping
        LayoutRect r = hull.Rect;

        // The hull is the group's "tetris" outline — the rooms' cells merged along shared edges, corners
        // rounded. It's borderless: only the fill carries the grouping. A "live" group (active: the selection,
        // "here", or hover sits in it) lifts to the bright fill so "this is the group I'm in" reads at a
        // glance; every resting group sits far dimmer so it whispers the grouping rather than colour-washing
        // the map.
        bool bright = active || selected;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = HullGeometry(hull.Loops, ox, oy, 18 * s),
            Fill = new SolidColorBrush(c, bright ? 0.11 : main ? 0.005 : 0.012),
        };
        canvas.Children.Add(path);

        PaintBadge(canvas, hull.Group, c, main, bright, r.Left + ox, r.Top + oy, s);
    }

    // Turn the hull's rectilinear rings into a filled path with rounded corners: each corner is cut back by
    // `radius` (clamped to half its shorter edge, so short notches stay clean) and bridged with a quadratic —
    // which rounds convex and concave corners alike. Holes (a group encircling an empty cell) are extra rings
    // under the even-odd rule. Points already carry the camera offset (ox, oy).
    private static StreamGeometry HullGeometry(IReadOnlyList<IReadOnlyList<LayoutPoint>> loops, double ox, double oy, double radius)
    {
        var geo = new StreamGeometry();
        using StreamGeometryContext ctx = geo.Open();
        ctx.SetFillRule(FillRule.EvenOdd);

        foreach (IReadOnlyList<LayoutPoint> loop in loops)
        {
            int n = loop.Count;
            if (n < 3) continue;
            var p = new Point[n];
            for (int i = 0; i < n; i++) p[i] = new Point(loop[i].X + ox, loop[i].Y + oy);

            double R(int i)
            {
                Point prev = p[(i - 1 + n) % n], next = p[(i + 1) % n];
                return Math.Min(radius, Math.Min(Dist(p[i], prev), Dist(p[i], next)) / 2);
            }
            Point In(int i) => Toward(p[i], p[(i - 1 + n) % n], R(i));   // point on the incoming edge
            Point Out(int i) => Toward(p[i], p[(i + 1) % n], R(i));      // point on the outgoing edge

            ctx.BeginFigure(Out(0), isFilled: true);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                ctx.LineTo(In(j));                    // straight run to just before the next corner
                ctx.QuadraticBezierTo(p[j], Out(j));  // round the corner through the true vertex
            }
            ctx.EndFigure(true);
        }
        return geo;
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Point Toward(Point from, Point to, double d)
    {
        double len = Dist(from, to);
        if (len < 1e-6) return from;
        double t = d / len;
        return new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
    }

    private static void PaintBadge(Canvas canvas, SpatialGroup group, Color c, bool main, bool bright, double x, double y, double s)
    {
        var dot = new Ellipse { Width = 7 * s, Height = 7 * s, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = group.Name, FontFamily = Mono, FontSize = 11.5 * s,
            Foreground = new SolidColorBrush(main ? Color.Parse("#0D0D11") : c), VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Background = new SolidColorBrush(main ? Color.FromArgb(0xEB, 0xC5, 0xD0, 0xE0)
                                                  : Color.FromArgb(0xD2, 0x0D, 0x10, 0x17)),
            BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8 * s, 3 * s, 9 * s, 3 * s),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, Children = { dot, text } },
        };
        badge.Opacity = bright ? 1.0 : 0.4; // labels dim with their group; only the live one reads at full strength
        Canvas.SetLeft(badge, x + 2 * s);
        Canvas.SetTop(badge, y - 11 * s); // ride the hull's top edge, like the metro route badge
        canvas.Children.Add(badge);
    }

    // A room tile: the board's screen-mockup look, but its windows/caption/border tinted to the group colour.
    private static Control Tile(SpatialRoom room, Color groupColor, double s)
    {
        double tileW = BaseTileW * s, screenH = BaseScrH * s, capH = BaseCapH * s;
        bool empty = room.WindowCount == 0;
        Color border = room.Selected ? Focus : room.Here ? Here : groupColor;
        double bt = room.Selected || room.Here ? 2 : 1;

        var winCanvas = new Canvas { Width = tileW, Height = screenH };
        if (!empty)
        {
            Color win = Blend(groupColor, WinBase, 0.45);
            AddWin(winCanvas, 9 * s, 9 * s, 44 * s, 14 * s, win, 1.0);
            AddWin(winCanvas, 9 * s, 27 * s, 30 * s, 13 * s, win, 1.0);
            AddWin(winCanvas, tileW - 9 * s - 22 * s, 14 * s, 22 * s, 26 * s, win, 0.7);
        }
        AddCountBadge(winCanvas, room.WindowCount, s, screenH);

        var screen = new Border
        {
            Width = tileW, Height = screenH, Background = new SolidColorBrush(TileBg),
            BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(bt, bt, bt, 0),
            CornerRadius = new CornerRadius(8 * s, 8 * s, 0, 0), ClipToBounds = true, Child = winCanvas,
        };
        var cap = new Border
        {
            Width = tileW, Height = capH, Background = new SolidColorBrush(CapBg),
            BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(bt, 0, bt, bt),
            CornerRadius = new CornerRadius(0, 0, 8 * s, 8 * s),
            Child = new TextBlock
            {
                Text = room.Label, FontFamily = Mono, FontSize = 11 * s,
                Foreground = new SolidColorBrush(room.Selected ? Ink : InkSoft),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical, Width = tileW, Children = { screen, cap } };
        Control result = stack;

        if (room.Here)
        {
            var grid = new Grid { Width = tileW };
            grid.Children.Add(stack);
            grid.Children.Add(new Border
            {
                Width = 15 * s, Height = 15 * s, CornerRadius = new CornerRadius(8 * s),
                Background = new SolidColorBrush(Here), BorderBrush = new SolidColorBrush(CapBg), BorderThickness = new Thickness(1.5 * s),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3 * s, 3 * s, 0, 0), IsHitTestVisible = false,
            });
            result = grid;
        }

        if (room.Selected) result.RenderTransform = new TranslateTransform(0, -5 * s);
        return result;
    }

    private static void AddCountBadge(Canvas screen, int count, double s, double screenH)
    {
        double h = 16 * s;
        var badge = new Border
        {
            Height = h, MinWidth = h, Padding = new Thickness(5 * s, 0), CornerRadius = new CornerRadius(h / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x14, 0x1D)),
            Child = new TextBlock
            {
                Text = count.ToString(), FontFamily = Mono, FontSize = 10 * s, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Ink),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, 6 * s);
        Canvas.SetTop(badge, screenH - h - 6 * s);
        screen.Children.Add(badge);
    }

    private static void AddWin(Canvas c, double x, double y, double w, double h, Color color, double opacity)
    {
        var r = new Rectangle { Width = w, Height = h, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(color), Opacity = opacity };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R * (1 - t) + b.R * t), (byte)(a.G * (1 - t) + b.G * t), (byte)(a.B * (1 - t) + b.B * t));

    // ── ASCII room: a monospace box-drawing card, tinted to the group colour (mirrors AsciiPainter) ──

    private static void DrawAsciiRoom(Canvas canvas, SpatialRoom room, Color groupColor, Rect cell, double s)
    {
        Color colour = room.Selected ? Focus : room.Here ? Here : groupColor;
        var card = new TextBlock
        {
            Text = AsciiCard(room), FontFamily = Mono, FontSize = AsciiFont * s,
            Foreground = new SolidColorBrush(colour), Background = new SolidColorBrush(AsciiGround),
            LineHeight = AsciiFont * 1.28 * s,
        };
        card.Measure(Size.Infinity);
        Canvas.SetLeft(card, cell.X + (cell.Width - card.DesiredSize.Width) / 2);
        Canvas.SetTop(card, cell.Y + (cell.Height - card.DesiredSize.Height) / 2);
        canvas.Children.Add(card);
    }

    // Three monospace lines: ┌─ label ─┐ / │ ## 4 │ / └────────┘ (double-ruled when selected, "@ " when here).
    private static string AsciiCard(SpatialRoom room)
    {
        (char tl, char tr, char bl, char br, char h, char v) = room.Selected
            ? ('╔', '╗', '╚', '╝', '═', '║') : ('┌', '┐', '└', '┘', '─', '│');

        string title = Trunc((room.Here ? "@ " : "") + room.Label, AsciiInnerW - 2);
        string topRaw = h + " " + title + " ";
        string top = topRaw.Length >= AsciiInnerW ? topRaw[..AsciiInnerW] : topRaw + new string(h, AsciiInnerW - topRaw.Length);

        string wins = room.WindowCount == 0 ? "" : new string('#', Math.Min(room.WindowCount, AsciiInnerW - 4));
        string count = room.WindowCount.ToString();
        int padTo = AsciiInnerW - count.Length - 1;
        string mid = " " + wins;
        mid = (mid.Length > padTo ? mid[..Math.Max(0, padTo)] : mid).PadRight(padTo) + count + " ";
        if (mid.Length > AsciiInnerW) mid = mid[..AsciiInnerW];

        return $"{tl}{top}{tr}\n{v}{mid}{v}\n{bl}{new string(h, AsciiInnerW)}{br}";
    }

    private static string Trunc(string t, int max) => max <= 0 ? "" : t.Length <= max ? t : t[..max];

    // ── Metro room: a station donut with a label chip below, tinted to the group colour (mirrors MetroPainter) ──

    private static void DrawMetroRoom(Canvas canvas, SpatialRoom room, Color groupColor, Rect cell, double s)
    {
        double cx = cell.X + cell.Width / 2, cy = cell.Y + cell.Height * 0.42; // station up top, chip below it
        double rOut = 10 * s;
        bool empty = room.WindowCount == 0, marked = room.Selected || room.Here;
        double r = rOut;
        double ring = 3.5 * s;

        if (room.Here)
        {
            var halo = new Ellipse { Width = rOut * 3.4, Height = rOut * 3.4, Fill = new SolidColorBrush(Here) { Opacity = 0.18 } };
            Canvas.SetLeft(halo, cx - rOut * 1.7); Canvas.SetTop(halo, cy - rOut * 1.7);
            canvas.Children.Add(halo);
        }

        Color donut = room.Selected ? Focus : room.Here ? Here : groupColor;
        var dot = new Ellipse
        {
            Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(MetroBg),
            Stroke = new SolidColorBrush(donut), StrokeThickness = ring,
        };
        Canvas.SetLeft(dot, cx - r); Canvas.SetTop(dot, cy - r);
        canvas.Children.Add(dot);

        if (room.Selected)
        {
            double fr = rOut + 6 * s;
            var focus = new Ellipse { Width = fr * 2, Height = fr * 2, Stroke = new SolidColorBrush(Focus), StrokeThickness = 2 * s };
            Canvas.SetLeft(focus, cx - fr); Canvas.SetTop(focus, cy - fr);
            canvas.Children.Add(focus);
        }

        // The label chip below the station.
        Color fill = room.Selected ? Focus : room.Here ? Here : Blend(ChipBase, groupColor, 0.13);
        Color ink = marked ? ChipInk : Blend(groupColor, ChipBase, 0.48);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s };
        content.Children.Add(new TextBlock
        {
            Text = room.Label, FontFamily = Sans, FontSize = 12.5 * s,
            FontWeight = marked ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(ink), VerticalAlignment = VerticalAlignment.Center,
        });
        if (!empty)
            content.Children.Add(new TextBlock
            {
                Text = room.WindowCount.ToString(), FontFamily = Mono, FontSize = 10 * s, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(marked ? Color.FromArgb(0xB4, ChipInk.R, ChipInk.G, ChipInk.B) : Blend(groupColor, ChipBase, 0.58)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        var chip = new Border
        {
            Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(9 * s),
            Padding = new Thickness(9 * s, 3 * s), Child = content,
        };
        if (!marked) { chip.BorderBrush = new SolidColorBrush(Blend(MetroBg, groupColor, 0.44)); chip.BorderThickness = new Thickness(Math.Max(1, s)); }
        chip.Measure(Size.Infinity);
        Canvas.SetLeft(chip, cx - chip.DesiredSize.Width / 2);
        Canvas.SetTop(chip, cy + rOut + 10 * s);
        canvas.Children.Add(chip);
    }
}
