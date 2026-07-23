using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// Renders the Model-P board in the docs/design/p-vs-q.html style, laid out as the F2 vertical model:
/// the <b>main timeline</b> (a row of desktop "tiles") is the pivot, with the fixed group stack split
/// around it — the groups before <see cref="NavMap.TopPosition"/> stack above main, the rest below,
/// the current group sitting directly beneath main. The board fills the whole screen (F3, no bounding
/// box): every row is centred horizontally on its own cursor, and the sequence scrolls vertically so
/// the current row sits on the screen's centre line. Rows wider than the screen extend under the edges
/// and are clipped by the canvas. Tiles can be made clickable (interactive map) to jump to a desktop.
/// Shared by the transient flash and the interactive map (<see cref="MapOverlay"/>).
/// </summary>
internal static class BoardView
{
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Color.Parse("#2A3444"), TileWin = Color.Parse("#374357");
    private static readonly Color StrBg = Color.Parse("#3A2E18"), StrBorder = Color.Parse("#6A5124"), StrWin = Color.Parse("#C9922F"), StrInk = Color.Parse("#E8A23D");
    private static readonly Color CapBg = Color.Parse("#161C27"), Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8"), InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Color.Parse("#6EA8FF");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    /// <summary>
    /// Lay the board out to fill a <paramref name="screenW"/>×<paramref name="screenH"/> surface,
    /// centred on the current row (vertically) and each row's cursor (horizontally).
    /// </summary>
    public static Control Render(NavMap map, double screenW, double screenH, double s = 1.0,
                                 Action<int>? onTopClick = null, Action<int, int>? onGroupClick = null,
                                 Action<int>? onTopDelete = null, Action<int, int>? onGroupDelete = null)
    {
        double tileW = 96 * s, scrH = 50 * s, capH = 22 * s, gap = 14 * s;
        double tileH = scrH + capH, lift = 6 * s, scopePad = 10 * s, labelH = 20 * s;
        double vgap = 26 * s;                 // vertical gap between rows (holds the connector spine)
        double mainLabelH = 18 * s;

        double cx = screenW / 2, cy = screenH / 2;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // ── The vertical sequence of rows: groups above main, main, then groups below. ──
        int split = Math.Clamp(map.TopPosition, 0, map.Groups.Count);
        var rows = new List<Row>();
        for (int gi = 0; gi < split; gi++) rows.Add(GroupRow(map.Groups[gi], s, tileW, scrH, capH, gap, scopePad, labelH, lift, onGroupClick, onGroupDelete));
        int mainRowIndex = rows.Count;
        rows.Add(MainRow(map, s, tileW, scrH, capH, gap, lift, mainLabelH, onTopClick, onTopDelete));
        for (int gi = split; gi < map.Groups.Count; gi++) rows.Add(GroupRow(map.Groups[gi], s, tileW, scrH, capH, gap, scopePad, labelH, lift, onGroupClick, onGroupDelete));

        // Which row is the user actually on? main when OnTop, else the current group — which may be
        // above OR below main. A group above main maps to a row before mainRowIndex; one below maps to
        // gi+1 (main occupies the slot at `split`). This row gets centred on screen.
        int currentRow = mainRowIndex;
        if (!map.OnTop)
        {
            for (int gi = 0; gi < map.Groups.Count; gi++)
                if (map.Groups[gi].IsCurrentLevel) { currentRow = gi < split ? gi : gi + 1; break; }
        }
        currentRow = Math.Clamp(currentRow, 0, rows.Count - 1);

        // Stack rows top→bottom, then shift the whole column so the current row is centred on screen.
        var yTop = new double[rows.Count];
        double run = 0;
        for (int i = 0; i < rows.Count; i++) { yTop[i] = run; run += rows[i].Height + vgap; }
        double curCentre = yTop[currentRow] + rows[currentRow].Height / 2;
        double offset = cy - curCentre;

        // Connector spine: a thin vertical line through cx joining consecutive rows (their cursors all
        // line up on cx), so the stack reads as one timeline.
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            double from = yTop[i] + offset + rows[i].Height;
            double to = yTop[i + 1] + offset;
            if (to <= from) continue;
            var line = new Rectangle { Width = Math.Max(2, 2 * s), Height = to - from, Fill = new SolidColorBrush(StrBorder), Opacity = 0.7 };
            Canvas.SetLeft(line, cx - 1);
            Canvas.SetTop(line, from);
            canvas.Children.Add(line);
        }

        for (int i = 0; i < rows.Count; i++)
            rows[i].Place(canvas, cx, yTop[i] + offset);

        return canvas;
    }

    // A laid-out row: knows its own height and how to place its content on the board canvas, centred
    // horizontally on cx (its cursor tile lands on the centre column).
    private sealed record Row(double Height, Action<Canvas, double, double> Place);

    private static Row MainRow(NavMap map, double s, double tileW, double scrH, double capH, double gap,
                               double lift, double labelH, Action<int>? onClick, Action<int>? onDelete)
    {
        double tileH = scrH + capH;
        double height = labelH + lift + tileH;
        return new Row(height, (canvas, cx, y) =>
        {
            var label = new TextBlock
            {
                Text = "▸ main", FontFamily = Mono, FontSize = 11 * s, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(map.OnTop ? Focus : InkFaint),
            };
            Canvas.SetLeft(label, cx - 24 * s);
            Canvas.SetTop(label, y);
            canvas.Children.Add(label);

            double rowY = y + labelH + lift;
            double originX = cx - (map.TopCursor * (tileW + gap) + tileW / 2); // centre the cursor tile on cx
            for (int i = 0; i < map.TopRow.Count; i++)
            {
                int idx = i;
                Control tile = Tile(map.TopRow[i].Label, isStream: false, map.OnTop && map.TopRow[i].IsCurrent, s,
                                    tileW, scrH, capH,
                                    onClick is null ? null : () => onClick(idx),
                                    onDelete is null ? null : () => onDelete(idx));
                Canvas.SetLeft(tile, originX + i * (tileW + gap));
                Canvas.SetTop(tile, rowY);
                canvas.Children.Add(tile);
            }
        });
    }

    private static Row GroupRow(NavMapGroup g, double s, double tileW, double scrH, double capH, double gap,
                                double scopePad, double labelH, double lift,
                                Action<int, int>? onClick, Action<int, int>? onDelete)
    {
        int groupIndex = g.Index;
        Action<int>? clickForThisGroup = onClick is null ? null : j => onClick(groupIndex, j);
        Action<int>? deleteForThisGroup = onDelete is null ? null : j => onDelete(groupIndex, j);
        Control box = BuildGroupBox(g, s, tileW, scrH, capH, gap, scopePad, clickForThisGroup, deleteForThisGroup);
        box.Opacity = g.IsCurrentLevel ? 1.0 : 0.45;
        box.Measure(Size.Infinity);
        double height = box.DesiredSize.Height + lift; // lift = headroom for the focused-tile pop

        return new Row(height, (canvas, cx, y) =>
        {
            // Centre the box on its own cursor: box origin + internal pad + cursor tile centre == cx.
            double boxX = cx - (scopePad + g.Cursor * (tileW + gap) + tileW / 2);
            Canvas.SetLeft(box, boxX);
            Canvas.SetTop(box, y + lift);
            canvas.Children.Add(box);
        });
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
