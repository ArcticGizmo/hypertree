using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// Renders the Model-P board in the docs/design/p-vs-q.html style: a top row of desktop "tiles" (a
/// screen with mini-window bars + a caption) for the ungrouped desktops, and the groups as amber
/// boxes stacked beneath in carousel order (active/nearest first). The current desktop is outlined
/// and the board is translated so it stays centred as you navigate. Non-current groups are dimmed
/// ("resting"). Tiles can be made clickable (overlay) to jump straight to a desktop.
/// Shared by the flash (<see cref="HudWindow"/>) and the interactive overlay (<see cref="MapOverlay"/>).
/// </summary>
internal static class BoardView
{
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Color.Parse("#2A3444"), TileWin = Color.Parse("#374357");
    private static readonly Color StrBg = Color.Parse("#3A2E18"), StrBorder = Color.Parse("#6A5124"), StrWin = Color.Parse("#C9922F"), StrInk = Color.Parse("#E8A23D");
    private static readonly Color CapBg = Color.Parse("#161C27"), Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8"), InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Color.Parse("#6EA8FF");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    public static Control Render(NavMap map, double s = 1.0, int maxGroups = int.MaxValue,
                                 Action<int>? onTopClick = null, Action<int, int>? onGroupClick = null,
                                 Action<int>? onTopDelete = null, Action<int, int>? onGroupDelete = null)
    {
        double tileW = 96 * s, screenH = 50 * s, capH = 22 * s, gap = 14 * s, conn = 22 * s;
        double tileH = screenH + capH, lift = 6 * s, top = 14 * s, pad = 22 * s;
        double scopePad = 10 * s, labelH = 20 * s, groupGap = 12 * s;

        double viewportW = (tileW + gap) * 6 + pad * 2;
        double cx = viewportW / 2;
        double rowY = top + lift;

        // ── Every row is centred on its own cursor (the position you'd return to), so the "current"
        //    tile of each row lines up on the centre column and moving between rows never slides
        //    the layout sideways. ──
        double topOffset = cx - (map.TopCursor * (tileW + gap) + tileW / 2);

        var canvas = new Canvas { Width = viewportW, ClipToBounds = true };
        double bottom = rowY + tileH;

        int shown = Math.Min(map.Groups.Count, maxGroups);
        double firstBoxY = rowY + tileH + conn;

        // Connector line down to the nearest group box (both centred → vertical at cx).
        if (shown > 0)
        {
            var line = new Rectangle { Width = Math.Max(2, 2 * s), Height = conn, Fill = new SolidColorBrush(StrBorder) };
            Canvas.SetLeft(line, cx - 1);
            Canvas.SetTop(line, rowY + tileH);
            canvas.Children.Add(line);
        }

        for (int d = 0; d < shown; d++)
        {
            NavMapGroup g = map.Groups[d];
            int groupIndex = g.Index;

            // Centre each group on its own cursor (resume point), so returning to any group lands
            // its remembered desktop on the centre column — aligned under the top-row cursor.
            double boxX = cx - (scopePad + g.Cursor * (tileW + gap) + tileW / 2);
            double boxY = firstBoxY + d * ( labelH + 6 * s + tileH + scopePad * 2 + groupGap );

            Action<int>? clickForThisGroup = onGroupClick is null ? null : j => onGroupClick(groupIndex, j);
            Action<int>? deleteForThisGroup = onGroupDelete is null ? null : j => onGroupDelete(groupIndex, j);
            Control box = BuildGroupBox(g, s, tileW, screenH, capH, gap, scopePad, clickForThisGroup, deleteForThisGroup);
            box.Opacity = g.IsCurrentLevel ? 1.0 : Math.Max(0.22, (map.OnTop ? 0.55 : 0.4) - d * 0.12);
            Canvas.SetLeft(box, boxX);
            Canvas.SetTop(box, boxY);
            canvas.Children.Add(box);

            bottom = boxY + labelH + 6 * s + tileH + scopePad * 2;
        }

        if (map.Groups.Count > shown)
        {
            var more = new TextBlock
            {
                Text = $"+{map.Groups.Count - shown} more", FontFamily = Mono, FontSize = 10 * s,
                Foreground = new SolidColorBrush(InkFaint),
            };
            Canvas.SetLeft(more, cx - 30 * s);
            Canvas.SetTop(more, bottom + 4 * s);
            canvas.Children.Add(more);
            bottom += 16 * s;
        }

        // ── Top-row tiles (drawn last so they sit above the connector). ──
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            bool focused = map.OnTop && map.TopRow[i].IsCurrent;
            int idx = i;
            Control tile = Tile(map.TopRow[i].Label, isStream: false, focused, s, tileW, screenH, capH,
                                onTopClick is null ? null : () => onTopClick(idx),
                                onTopDelete is null ? null : () => onTopDelete(idx));
            Canvas.SetLeft(tile, i * (tileW + gap) + topOffset);
            Canvas.SetTop(tile, rowY);
            canvas.Children.Add(tile);
        }

        canvas.Height = bottom + pad;
        return canvas;
    }

    private static Control BuildGroupBox(NavMapGroup g, double s, double tileW, double screenH, double capH,
                                         double gap, double scopePad, Action<int>? onDeskClick, Action<int>? onDeskDelete)
    {
        var label = new TextBlock
        {
            Text = "● " + g.Name + (g.IsCurrentLevel ? "" : "  · resting"),
            FontFamily = Mono, FontSize = 11 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(StrInk), Margin = new Thickness(2 * s, 0, 0, 6 * s),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap };
        for (int j = 0; j < g.Desktops.Count; j++)
        {
            int idx = j;
            row.Children.Add(Tile(g.Desktops[j].Label, isStream: true, g.Desktops[j].IsCurrent, s,
                                  tileW, screenH, capH,
                                  onDeskClick is null ? null : () => onDeskClick(idx),
                                  onDeskDelete is null ? null : () => onDeskDelete(idx)));
        }

        return new Border
        {
            Background = new SolidColorBrush(StrBg),
            BorderBrush = new SolidColorBrush(StrBorder),
            BorderThickness = new Thickness(Math.Max(1, 1.5 * s)),
            CornerRadius = new CornerRadius(12 * s),
            Padding = new Thickness(scopePad),
            Child = new StackPanel { Orientation = Orientation.Vertical, Children = { label, row } },
        };
    }

    private static Control Tile(string caption, bool isStream, bool focused, double s,
                                double tileW, double screenH, double capH, Action? onClick, Action? onDelete = null)
    {
        Color border = focused ? Focus : (isStream ? StrBorder : TileBorder);
        double bt = focused ? 2 : 1;

        var winCanvas = new Canvas { Width = tileW, Height = screenH };
        Color win = isStream ? StrWin : TileWin;
        AddWin(winCanvas, 9 * s, 9 * s, 44 * s, 14 * s, win, 1.0);
        AddWin(winCanvas, 9 * s, 27 * s, 30 * s, 13 * s, win, 1.0);
        AddWin(winCanvas, tileW - 9 * s - 22 * s, 14 * s, 22 * s, 26 * s, win, 0.7);

        var screen = new Border
        {
            Width = tileW, Height = screenH,
            Background = new SolidColorBrush(isStream ? StrBg : TileBg),
            BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(bt, bt, bt, 0),
            CornerRadius = new CornerRadius(8 * s, 8 * s, 0, 0), ClipToBounds = true, Child = winCanvas,
        };
        var cap = new Border
        {
            Width = tileW, Height = capH,
            Background = new SolidColorBrush(CapBg),
            BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(bt, 0, bt, bt),
            CornerRadius = new CornerRadius(0, 0, 8 * s, 8 * s),
            Child = new TextBlock
            {
                Text = caption, FontFamily = Mono, FontSize = 11 * s,
                Foreground = new SolidColorBrush(focused ? Ink : (isStream ? StrInk : InkSoft)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical, Width = tileW, Children = { screen, cap } };
        if (onClick is not null)
        {
            stack.Cursor = new Cursor(StandardCursorType.Hand);
            stack.PointerPressed += (_, _) => onClick();
        }

        Control result = stack;
        if (onDelete is not null)
        {
            // Overlay a small delete badge in the screen's top-right corner. Its own pointer handler is
            // marked handled so it doesn't also trigger the tile's click-to-navigate.
            var badge = new Border
            {
                Width = 17 * s, Height = 17 * s,
                CornerRadius = new CornerRadius(9 * s),
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xC0, 0x3A, 0x2E)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3 * s, 3 * s, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = "×", FontSize = 12 * s, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.PointerPressed += (_, e) => { e.Handled = true; onDelete(); };

            var grid = new Grid { Width = tileW };
            grid.Children.Add(stack);
            grid.Children.Add(badge);
            result = grid;
        }

        if (focused) result.RenderTransform = new TranslateTransform(0, -6 * s);
        return result;
    }

    private static void AddWin(Canvas c, double x, double y, double w, double h, Color color, double opacity)
    {
        var r = new Rectangle { Width = w, Height = h, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(color), Opacity = opacity };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }
}
