using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Layout;
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
                                 IList<(DesktopId Id, Rect Rect)>? hits = null, Guid? selectedGroup = null)
    {
        var layout = new SpatialLayout(scene, Metrics(s));
        camera.Update(layout, screenW, screenH);
        double ox = camera.OffsetX, oy = camera.OffsetY;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // Hulls first (behind the rooms), then their name badges, then the rooms on top.
        foreach (GroupHull hull in layout.Hulls(BaseHullPad * s, BaseHullPad * s))
            PaintHull(canvas, hull, ox, oy, s, selectedGroup is { } sg && hull.Group.Id == sg);

        foreach (PlacedRoom placed in layout.Rooms)
        {
            DesktopId id = placed.Room.Id;
            Color groupColor = Color.Parse(scene.Groups.First(g => g.Id == placed.Room.GroupId).Color);
            Control tile = Tile(placed.Room, groupColor, s,
                                onClick is null ? null : () => onClick(id),
                                onActivate is null ? null : () => onActivate(id));
            Canvas.SetLeft(tile, placed.Rect.Left + ox);
            Canvas.SetTop(tile, placed.Rect.Top + oy);
            canvas.Children.Add(tile);
            hits?.Add((id, new Rect(placed.Rect.Left + ox, placed.Rect.Top + oy, placed.Rect.Width, placed.Rect.Height)));
        }

        return canvas;
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
    private static Control Tile(SpatialRoom room, Color groupColor, double s,
                                Action? onClick = null, Action? onActivate = null)
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
        if (onClick is not null || onActivate is not null)
        {
            stack.Cursor = new Cursor(StandardCursorType.Hand);
            stack.PointerPressed += (_, e) =>
            {
                if (e.ClickCount >= 2) onActivate?.Invoke();
                else onClick?.Invoke();
            };
        }
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
}
