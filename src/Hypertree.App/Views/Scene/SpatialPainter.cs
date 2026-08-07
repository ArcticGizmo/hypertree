using Avalonia;
using Avalonia.Collections;
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
/// group's colour) sitting inside translucent group hulls, one hull per contiguous fragment so a scattered
/// group reads as broken. It only draws — <see cref="SpatialLayout"/> owns world placement and the shared
/// <see cref="MapCamera"/> owns panning, so the spatial model frames and moves exactly like the rows.
///
/// M1 is read-only (captures, previews); selection/drag/tidy interaction lands in later milestones.
/// </summary>
internal static class SpatialPainter
{
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Color.Parse("#2A3444");
    private static readonly Color CapBg = Color.Parse("#161C27");
    private static readonly Color Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8"), InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Color.Parse("#6EA8FF"), Here = Color.Parse("#34D399");
    private static readonly Color WinBase = Color.Parse("#374357");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");
    // ASCII / Metro room styling, matching AsciiPainter and MetroPainter so a room reads the same in either model.
    private static readonly Color AsciiGround = Color.Parse("#0C0F16");
    private static readonly Color MetroBg = Color.Parse("#0F131B"), ChipBase = Color.Parse("#111722"), ChipInk = Color.Parse("#0A0D12");
    private static readonly FontFamily Sans = new("Inter,Segoe UI,sans-serif");
    private const double AsciiFont = 13;
    private const int AsciiInnerW = 9;

    // Base geometry (unscaled). A room is the board tile; the grid stride leaves generous gaps so rooms and
    // their hulls have room to breathe (tile is 96×72, so these strides leave ~60px between neighbours).
    private const double BaseTileW = 96, BaseScrH = 50, BaseCapH = 22;
    private const double BaseStrideX = 158, BaseStrideY = 140, BaseHullPad = 16;
    private static double TileH => BaseScrH + BaseCapH; // 72

    /// <summary>The spatial metrics — a near-square grid, unlike the tall row pitch, since 2-D placement
    /// wants comparable breathing room on both axes.</summary>
    public static SceneMetrics Metrics(double s) => new(
        CellStride: BaseStrideX * s, CellWidth: BaseTileW * s, CellHeight: TileH * s,
        RowPitch: BaseStrideY * s, RowHeight: TileH * s);

    public static Control Render(SpatialScene scene, double screenW, double screenH, double s, MapCamera camera,
                                 Action<DesktopId>? onClick = null, Action<DesktopId>? onActivate = null,
                                 IList<(DesktopId Id, Rect Rect)>? hits = null, Guid? selectedGroup = null,
                                 MapStyle style = MapStyle.Board, IDictionary<DesktopId, Control>? roomHosts = null)
    {
        var layout = new SpatialLayout(scene, Metrics(s));
        camera.Update(layout, screenW, screenH);
        double ox = camera.OffsetX, oy = camera.OffsetY;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // Hulls first (behind the rooms), then their name badges, then the rooms on top. The hull is the
        // spatial stand-in for the row model's branch box / metro route, so it stays whatever the room style.
        foreach (GroupHull hull in layout.Hulls(BaseHullPad * s, BaseHullPad * s))
            PaintHull(canvas, hull, ox, oy, s, selectedGroup is { } sg && hull.Group.Id == sg);

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

        return canvas;
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

    private static void PaintHull(Canvas canvas, GroupHull hull, double ox, double oy, double s, bool selected)
    {
        Color c = Color.Parse(hull.Group.Color);
        bool main = hull.Group.IsMain; // the ungrouped bucket: barely-there, dashed, no deliberate grouping
        LayoutRect r = hull.Rect;

        // A selected group lifts to a stronger fill and a blue selection stroke, the same accent the room
        // selection uses, so "this group is active" reads at a glance. Resting fills are kept very faint —
        // the hull should whisper the grouping, not colour-wash the rooms inside it.
        var rect = new Rectangle
        {
            Width = r.Width, Height = r.Height, RadiusX = 20 * s, RadiusY = 20 * s,
            Fill = new SolidColorBrush(c, selected ? 0.11 : main ? 0.02 : 0.045),
            Stroke = new SolidColorBrush(selected ? Focus : c, selected ? 1.0 : main ? 0.18 : 0.34),
            StrokeThickness = Math.Max(1, (selected ? 1.8 : 1.1) * s),
        };
        if (main && !selected) rect.StrokeDashArray = new AvaloniaList<double> { 2, 4 };
        Canvas.SetLeft(rect, r.Left + ox);
        Canvas.SetTop(rect, r.Top + oy);
        canvas.Children.Add(rect);

        if (hull.Primary) PaintBadge(canvas, hull.Group, c, main, r.Left + ox, r.Top + oy, s);
    }

    private static void PaintBadge(Canvas canvas, SpatialGroup group, Color c, bool main, double x, double y, double s)
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
                Foreground = new SolidColorBrush(room.Selected ? Ink : (empty ? InkFaint : InkSoft)),
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

        if (empty && !room.Selected && !room.Here) result.Opacity = 0.5;
        if (room.Selected) result.RenderTransform = new TranslateTransform(0, -5 * s);
        return result;
    }

    private static void AddCountBadge(Canvas screen, int count, double s, double screenH)
    {
        double h = 16 * s;
        var badge = new Border
        {
            Height = h, MinWidth = h, Padding = new Thickness(5 * s, 0), CornerRadius = new CornerRadius(h / 2),
            Background = new SolidColorBrush(count == 0 ? Color.FromArgb(0x33, 0x16, 0x1C, 0x27)
                                                        : Color.FromArgb(0xCC, 0x0F, 0x14, 0x1D)),
            Child = new TextBlock
            {
                Text = count.ToString(), FontFamily = Mono, FontSize = 10 * s, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(count == 0 ? InkFaint : Ink),
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
        bool empty = room.WindowCount == 0;
        Color colour = room.Selected ? Focus : room.Here ? Here : (empty ? Blend(groupColor, AsciiGround, 0.5) : groupColor);
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
        double r = empty && !marked ? rOut * 0.66 : rOut;
        double ring = empty && !marked ? 2.5 * s : 3.5 * s;

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
        Color ink = marked ? ChipInk : Blend(groupColor, ChipBase, empty ? 0.64 : 0.48);
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
        if (!marked) { chip.BorderBrush = new SolidColorBrush(Blend(MetroBg, groupColor, empty ? 0.3 : 0.44)); chip.BorderThickness = new Thickness(Math.Max(1, s)); }
        chip.Measure(Size.Infinity);
        Canvas.SetLeft(chip, cx - chip.DesiredSize.Width / 2);
        Canvas.SetTop(chip, cy + rOut + 10 * s);
        canvas.Children.Add(chip);
    }
}
