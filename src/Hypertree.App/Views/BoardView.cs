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
/// the <b>main timeline</b> (a row of desktop "tiles") is the pivot, with the fixed branch stack split
/// around it — the branches before <see cref="NavMap.TopPosition"/> stack above main, the rest below,
/// the current branch sitting directly beneath main. The board fills the whole screen (F3, no bounding
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
    private static readonly Color Here = Color.Parse("#34D399"); // "you are here" — distinct from the blue target
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    /// <summary>
    /// Lay the board out to fill a <paramref name="screenW"/>×<paramref name="screenH"/> surface,
    /// centred on the current row (vertically) and each row's cursor (horizontally).
    /// </summary>
    public static Control Render(NavMap map, double screenW, double screenH, double s = 1.0,
                                 Action<int>? onTopClick = null, Action<int, int>? onBranchClick = null,
                                 Action<int>? onTopDelete = null, Action<int, int>? onBranchDelete = null,
                                 Action<int>? onTopActivate = null, Action<int, int>? onBranchActivate = null)
    {
        double tileW = 96 * s, scrH = 50 * s, capH = 22 * s, gap = 14 * s;
        double tileH = scrH + capH, lift = 6 * s, scopePad = 10 * s, labelH = 20 * s;
        double vgap = 26 * s;                 // vertical gap between rows (holds the connector spine)
        double mainLabelH = 18 * s;

        double cx = screenW / 2, cy = screenH / 2;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // ── The vertical sequence of rows: branches above main, main, then branches below. ──
        int split = Math.Clamp(map.TopPosition, 0, map.Branches.Count);
        var rows = new List<Row>();
        for (int gi = 0; gi < split; gi++) rows.Add(BranchRow(map.Branches[gi], s, tileW, scrH, capH, gap, scopePad, labelH, lift, onBranchClick, onBranchDelete, onBranchActivate));
        int mainRowIndex = rows.Count;
        rows.Add(MainRow(map, s, tileW, scrH, capH, gap, lift, mainLabelH, onTopClick, onTopDelete, onTopActivate));
        for (int gi = split; gi < map.Branches.Count; gi++) rows.Add(BranchRow(map.Branches[gi], s, tileW, scrH, capH, gap, scopePad, labelH, lift, onBranchClick, onBranchDelete, onBranchActivate));

        // Which row do we centre on? The row holding the current (IsCurrent) tile — main when OnTop,
        // else the branch that contains it (which may be above OR below main: a branch above main maps to
        // a row before mainRowIndex, one below to gi+1, since main occupies the slot at `split`).
        int currentRow = mainRowIndex;
        if (!map.OnTop)
        {
            for (int gi = 0; gi < map.Branches.Count; gi++)
                if (map.Branches[gi].Desktops.Any(d => d.IsCurrent)) { currentRow = gi < split ? gi : gi + 1; break; }
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
                               double lift, double labelH, Action<int>? onClick, Action<int>? onDelete,
                               Action<int>? onActivate)
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
                Control tile = Tile(map.TopRow[i].Label, isStream: false, map.OnTop && map.TopRow[i].IsCurrent,
                                    map.TopRow[i].IsHere, map.TopRow[i].WindowCount, s, tileW, scrH, capH,
                                    onClick is null ? null : () => onClick(idx),
                                    onDelete is null ? null : () => onDelete(idx),
                                    onActivate is null ? null : () => onActivate(idx));
                Canvas.SetLeft(tile, originX + i * (tileW + gap));
                Canvas.SetTop(tile, rowY);
                canvas.Children.Add(tile);
            }
        });
    }

    private static Row BranchRow(NavMapBranch g, double s, double tileW, double scrH, double capH, double gap,
                                double scopePad, double labelH, double lift,
                                Action<int, int>? onClick, Action<int, int>? onDelete, Action<int, int>? onActivate)
    {
        int branchIndex = g.Index;
        Action<int>? clickForThisBranch = onClick is null ? null : j => onClick(branchIndex, j);
        Action<int>? deleteForThisBranch = onDelete is null ? null : j => onDelete(branchIndex, j);
        Action<int>? activateForThisBranch = onActivate is null ? null : j => onActivate(branchIndex, j);
        Control box = BuildBranchBox(g, s, tileW, scrH, capH, gap, scopePad, clickForThisBranch, deleteForThisBranch, activateForThisBranch);
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

    private static Control BuildBranchBox(NavMapBranch g, double s, double tileW, double screenH, double capH,
                                         double gap, double scopePad, Action<int>? onDeskClick, Action<int>? onDeskDelete,
                                         Action<int>? onDeskActivate)
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
            row.Children.Add(Tile(g.Desktops[j].Label, isStream: true, g.Desktops[j].IsCurrent,
                                  g.Desktops[j].IsHere, g.Desktops[j].WindowCount, s, tileW, screenH, capH,
                                  onDeskClick is null ? null : () => onDeskClick(idx),
                                  onDeskDelete is null ? null : () => onDeskDelete(idx),
                                  onDeskActivate is null ? null : () => onDeskActivate(idx)));
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

    private static Control Tile(string caption, bool isStream, bool focused, bool here, int windowCount, double s,
                                double tileW, double screenH, double capH, Action? onClick, Action? onDelete = null,
                                Action? onActivate = null)
    {
        // The focused/target tile wins the border colour; a non-target "here" tile gets the green
        // marker so current-vs-destination (and the distance between) reads at a glance.
        Color border = focused ? Focus : here ? Here : (isStream ? StrBorder : TileBorder);
        double bt = focused || here ? 2 : 1;
        bool empty = windowCount == 0;

        var winCanvas = new Canvas { Width = tileW, Height = screenH };
        Color win = isStream ? StrWin : TileWin;
        // An empty desktop draws no window glyphs — the tile reads as bare, reinforcing the "0" badge.
        if (!empty)
        {
            AddWin(winCanvas, 9 * s, 9 * s, 44 * s, 14 * s, win, 1.0);
            AddWin(winCanvas, 9 * s, 27 * s, 30 * s, 13 * s, win, 1.0);
            AddWin(winCanvas, tileW - 9 * s - 22 * s, 14 * s, 22 * s, 26 * s, win, 0.7);
        }
        AddCountBadge(winCanvas, windowCount, s, tileW, screenH);

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
        if (onClick is not null || onActivate is not null)
        {
            stack.Cursor = new Cursor(StandardCursorType.Hand);
            // Single click selects (onClick); a double click activates (onActivate) — e.g. select vs. jump on
            // the interactive map. The first press of a double still fires onClick, so activate lands on the
            // clicked tile either way.
            stack.PointerPressed += (_, e) =>
            {
                if (e.ClickCount >= 2) onActivate?.Invoke();
                else onClick?.Invoke();
            };
        }

        Control result = stack;
        if (here || onDelete is not null)
        {
            var grid = new Grid { Width = tileW };
            grid.Children.Add(stack);

            // "You are here" marker: a green pip in the top-left corner (non-interactive), paired with
            // the green border above — so the current desktop stands apart from the blue target.
            if (here)
            {
                grid.Children.Add(new Border
                {
                    Width = 15 * s, Height = 15 * s, CornerRadius = new CornerRadius(8 * s),
                    Background = new SolidColorBrush(Here),
                    BorderBrush = new SolidColorBrush(CapBg), BorderThickness = new Thickness(1.5 * s),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(3 * s, 3 * s, 0, 0), IsHitTestVisible = false,
                });
            }

            // Delete badge in the top-right corner. Its pointer handler is marked handled so it doesn't
            // also trigger the tile's click-to-navigate.
            if (onDelete is not null)
            {
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
                grid.Children.Add(badge);
            }

            result = grid;
        }

        // An empty desktop (0 windows) reads dimmer, so populated desktops pop at a glance. The current
        // and "here" tiles stay full-strength so the cursor is never washed out.
        if (empty && !focused && !here) result.Opacity = 0.5;

        if (focused) result.RenderTransform = new TranslateTransform(0, -6 * s);
        return result;
    }

    // A small pill in the bottom-left of the screen area showing the window count — bright when the
    // desktop has windows, faint "0" when it's empty. Corners stay free for the here-pip / delete badge.
    private static void AddCountBadge(Canvas screen, int count, double s, double tileW, double screenH)
    {
        double h = 16 * s;
        var badge = new Border
        {
            Height = h, MinWidth = h, Padding = new Thickness(5 * s, 0),
            CornerRadius = new CornerRadius(h / 2),
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
}
