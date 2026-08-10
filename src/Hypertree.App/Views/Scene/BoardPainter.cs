using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Layout;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The tile-board theme as an <see cref="IScenePainter"/> — the docs/design/p-vs-q.html look: each desktop a
/// little screen mock-up with a caption, branches wrapped in a rounded box with a "● name" label, main
/// under a "▸ main" label, all joined by a thin connector spine. It only draws; the shared
/// <see cref="SceneRenderer"/> owns where everything lands, so it moves in lock-step with the metro theme.
/// </summary>
internal sealed class BoardPainter : IScenePainter
{
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Palette.Stroke, TileWin = Color.Parse("#374357");
    private static readonly Color StrBg = Color.Parse("#3A2E18"), StrBorder = Color.Parse("#6A5124"), StrWin = Color.Parse("#C9922F"), StrInk = Color.Parse("#E8A23D");
    private static readonly Color CapBg = Color.Parse("#161C27"), Ink = Palette.Ink, InkSoft = Palette.Muted, InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Palette.Accent;
    private static readonly Color Here = Palette.Here;
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // Base geometry (unscaled). The tile is the "cell"; the box/label headroom sets the row height.
    private const double BaseTileW = 96, BaseScrH = 50, BaseCapH = 22, BaseGap = 14;
    private const double BaseLift = 6, BaseScopePad = 10, BaseLabelH = 17, BaseLabelGap = 6, BaseMainLabelH = 18, BaseVGap = 26;
    private static double TileH => BaseScrH + BaseCapH;                       // 72
    private static double RowContentH => BaseLabelH + BaseLabelGap + TileH + 2 * BaseScopePad + BaseLift; // branch box + pop

    public SceneMetrics Metrics(double s) => new(
        CellStride: (BaseTileW + BaseGap) * s,
        CellWidth: BaseTileW * s,
        CellHeight: TileH * s,
        RowPitch: (RowContentH + BaseVGap) * s,
        RowHeight: RowContentH * s);

    // The branch box is bounded by its tiles (plus a little padding the band already covers), so there's no
    // trailing handle beyond the last cell.
    public double RowTrailing(SceneRow row, double s) => 0;

    public void PaintSpine(Canvas canvas, IReadOnlyList<(double X, double Y)> col0Centres, double s)
    {
        // A thin vertical connector joining consecutive rows through their column-0 stations — the left-side
        // successor of the old centre trunk (rows now align at their first desktop, not their cursor).
        for (int i = 0; i + 1 < col0Centres.Count; i++)
        {
            (double x, double y0) = col0Centres[i];
            double y1 = col0Centres[i + 1].Y;
            if (y1 <= y0) continue;
            var line = new Rectangle
            {
                Width = Math.Max(2, 2 * s), Height = y1 - y0,
                Fill = new SolidColorBrush(StrBorder), Opacity = 0.7,
            };
            Canvas.SetLeft(line, x - 1);
            Canvas.SetTop(line, y0);
            canvas.Children.Add(line);
        }
    }

    public void PaintRow(Canvas canvas, RowFrame frame, double s,
                         Action<int>? onClick, Action<int>? onActivate, Action<int>? onDelete)
    {
        IReadOnlyList<Rect> cells = frame.Cells;
        double tileH = TileH * s;
        double tileTop = frame.CentreY - tileH / 2;

        if (frame.Row.IsMain) PaintMainLabel(canvas, frame, tileTop, s);
        else PaintBranchBox(canvas, frame, tileTop, tileH, s);

        for (int c = 0; c < cells.Count; c++)
        {
            int col = c;
            SceneCell cell = frame.Row.Cells[c];
            Control tile = Tile(cell.Label, isStream: !frame.Row.IsMain, cell.Selected, cell.Here, cell.WindowCount, s,
                                BaseTileW * s, BaseScrH * s, BaseCapH * s,
                                onClick is null ? null : () => onClick(col),
                                onDelete is null ? null : () => onDelete(col),
                                onActivate is null ? null : () => onActivate(col));
            Canvas.SetLeft(tile, cells[c].X);
            Canvas.SetTop(tile, tileTop);
            canvas.Children.Add(tile);
        }
    }

    private static void PaintMainLabel(Canvas canvas, RowFrame frame, double tileTop, double s)
    {
        double x = frame.Cells.Count > 0 ? frame.Cells[0].X : frame.Band.X;
        var label = new TextBlock
        {
            Text = "▸ main", FontFamily = Mono, FontSize = 11 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(frame.Row.Active ? Focus : InkFaint),
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, tileTop - BaseMainLabelH * s);
        canvas.Children.Add(label);
    }

    private void PaintBranchBox(Canvas canvas, RowFrame frame, double tileTop, double tileH, double s)
    {
        if (frame.Cells.Count == 0) return;
        double scopePad = BaseScopePad * s, labelH = BaseLabelH * s, labelGap = BaseLabelGap * s, bt = Math.Max(1, 1.5 * s);

        double stripLeft = frame.Cells[0].X, stripRight = frame.Cells[^1].Right;
        double boxLeft = stripLeft - scopePad - bt;
        double boxTop = tileTop - labelGap - labelH - scopePad;
        double boxRight = stripRight + scopePad + bt;
        double boxBottom = tileTop + tileH + scopePad;

        var box = new Border
        {
            Width = boxRight - boxLeft, Height = boxBottom - boxTop,
            Background = new SolidColorBrush(StrBg), BorderBrush = new SolidColorBrush(StrBorder),
            BorderThickness = new Thickness(bt), CornerRadius = new CornerRadius(12 * s),
            Opacity = frame.Row.Active ? 1.0 : 0.45, // a resting branch recedes (reconciled to colour-dim in a later phase)
        };
        Canvas.SetLeft(box, boxLeft);
        Canvas.SetTop(box, boxTop);
        canvas.Children.Add(box);

        var label = new TextBlock
        {
            Text = "● " + frame.Row.Name, FontFamily = Mono, FontSize = 11 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(StrInk), Opacity = frame.Row.Active ? 1.0 : 0.45,
        };
        Canvas.SetLeft(label, stripLeft);
        Canvas.SetTop(label, boxTop + scopePad);
        canvas.Children.Add(label);
    }

    // ── Tile drawing (the board tile look; position-agnostic — the caller places it) ─────────────

    private static Control Tile(string caption, bool isStream, bool focused, bool here, int windowCount, double s,
                                double tileW, double screenH, double capH, Action? onClick, Action? onDelete = null,
                                Action? onActivate = null)
    {
        Color border = focused ? Focus : here ? Here : (isStream ? StrBorder : TileBorder);
        double bt = focused || here ? 2 : 1;
        bool empty = windowCount == 0;

        var winCanvas = new Canvas { Width = tileW, Height = screenH };
        Color win = isStream ? StrWin : TileWin;
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

            if (onDelete is not null)
            {
                var badge = new Border
                {
                    Width = 17 * s, Height = 17 * s, CornerRadius = new CornerRadius(9 * s),
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xC0, 0x3A, 0x2E)),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 3 * s, 3 * s, 0), Cursor = new Cursor(StandardCursorType.Hand),
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

        if (empty && !focused && !here) result.Opacity = 0.5;
        if (focused) result.RenderTransform = new TranslateTransform(0, -6 * s);
        return result;
    }

    private static void AddCountBadge(Canvas screen, int count, double s, double tileW, double screenH)
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
}
