using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hypertree.Layout;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The transit-diagram theme as an <see cref="IScenePainter"/>: each timeline a coloured route, each desktop
/// a station donut with a label chip, joined by a neutral vertical spine through the first station of every
/// line. It only draws; the shared <see cref="SceneRenderer"/> owns layout and camera, so it moves in
/// lock-step with the board theme. See docs/design/metro-map.md for the visual language.
/// </summary>
internal sealed class MetroPainter : IScenePainter
{
    private static readonly Color Bg = Color.Parse("#0F131B");
    private static readonly Color Trunk = Color.Parse("#3A4453");
    private static readonly Color MainLine = Color.Parse("#C5D0E0");
    private static readonly Color ChipBase = Color.Parse("#111722");
    private static readonly Color ChipInk = Color.Parse("#0A0D12");
    private static readonly Color Focus = Palette.Accent;
    private static readonly Color Here = Palette.Here;
    private static readonly FontFamily Sans = new("Inter,Segoe UI,sans-serif");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private const double BaseStride = 156, BaseLineW = 8, BaseROut = 10, BaseVGap = 140, BaseCellH = 96;

    private readonly bool _animate;
    public MetroPainter(bool animate = false) => _animate = animate;

    public SceneMetrics Metrics(double s) => new(
        CellStride: BaseStride * s,
        CellWidth: BaseStride * s,   // stations tile the strip: each cell is one stride wide
        CellHeight: BaseCellH * s,   // tall enough to cover the donut and the chip below it
        RowPitch: BaseVGap * s,
        RowHeight: BaseCellH * s);

    // A line's drag handle runs out to its route badge past the last station, so the reported band extends
    // beyond the last cell by the gap to the badge plus the badge itself (less the half-stride the cell band
    // already reaches). Mirrors the old MetroView band that ran out to the badge.
    public double RowTrailing(SceneRow row, double s)
        => Math.Max(0, 16 * s + EstimateBadgeWidth(row.Name, s) - BaseStride * s / 2);

    private static double EstimateBadgeWidth(string name, double s) => name.Length * 7.4 * s + 22 * s;

    public void PaintSpine(Canvas canvas, IReadOnlyList<(double X, double Y)> col0Centres, double s)
    {
        // The interchange trunk: a neutral vertical tie joining every line's first station — the dive/surface
        // axis, now on the shared left column rather than through the (moving) cursor.
        if (col0Centres.Count < 2) return;
        double top = col0Centres[0].Y, bottom = col0Centres[^1].Y;
        double x = col0Centres[0].X;
        var tie = new Rectangle
        {
            Width = Math.Max(2, 3 * s), Height = bottom - top, RadiusX = 1.5 * s, RadiusY = 1.5 * s,
            Fill = new SolidColorBrush(Trunk),
        };
        Canvas.SetLeft(tie, x - 1.5 * s);
        Canvas.SetTop(tie, top);
        canvas.Children.Add(tie);
    }

    public void PaintRow(Canvas canvas, RowFrame frame, double s,
                         Action<int>? onClick, Action<int>? onActivate, Action<int>? onDelete)
    {
        IReadOnlyList<Rect> cells = frame.Cells;
        int n = cells.Count;
        if (n == 0) return;

        Color colour = frame.Row.IsMain ? MainLine : ScenePaint.BranchColour(frame.Row.BranchIndex);
        double op = frame.Row.Active ? 1.0 : frame.Row.IsMain ? 0.82 : 0.5;
        double y = frame.CentreY;
        double lineW = BaseLineW * s, rOut = BaseROut * s;

        double firstX = CentreX(cells[0]), lastX = CentreX(cells[^1]), stride = BaseStride * s;
        double routeLeft = n <= 1 ? firstX - stride * 0.32 : firstX;
        double routeRight = n <= 1 ? firstX + stride * 0.32 : lastX;

        var route = new Rectangle
        {
            Width = routeRight - routeLeft, Height = lineW, RadiusX = lineW / 2, RadiusY = lineW / 2,
            Fill = new SolidColorBrush(Dim(colour, op)),
        };
        if (op >= 1.0)
            route.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 16, Color = colour, Opacity = 0.5 };
        Canvas.SetLeft(route, routeLeft);
        Canvas.SetTop(route, y - lineW / 2);
        canvas.Children.Add(route);

        AddRouteBadge(canvas, frame.Row.Name, colour, routeRight + 16 * s, y, s, op);

        for (int c = 0; c < n; c++)
        {
            int col = c;
            SceneCell cell = frame.Row.Cells[c];
            double sx = CentreX(cells[c]);
            AddStation(canvas, cell, colour, sx, y, rOut, s, op);
            AddChip(canvas, cell, colour, sx, y, rOut, s);

            ScenePaint.HitCell(canvas, cells[c],
                               onClick is null ? null : () => onClick(col),
                               onActivate is null ? null : () => onActivate(col));
        }
    }

    private static double CentreX(Rect r) => r.X + r.Width / 2;

    private void AddStation(Canvas canvas, SceneCell st, Color lineColour, double x, double y, double rOut, double s, double op)
    {
        bool empty = st.WindowCount == 0;
        bool marked = st.Selected || st.Here;
        double r = empty && !marked ? rOut * 0.66 : rOut;
        double ring = empty && !marked ? 2.5 * s : 3.5 * s;

        Color donut = st.Selected ? Focus : st.Here ? Here : Dim(lineColour, op);
        var dot = new Ellipse
        {
            Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(Bg),
            Stroke = new SolidColorBrush(donut), StrokeThickness = ring,
        };
        Canvas.SetLeft(dot, x - r);
        Canvas.SetTop(dot, y - r);
        canvas.Children.Add(dot);

        if (st.Here)
        {
            var halo = new Ellipse { Width = rOut * 3.4, Height = rOut * 3.4, Fill = new SolidColorBrush(Here) { Opacity = 0.18 } };
            Canvas.SetLeft(halo, x - rOut * 1.7);
            Canvas.SetTop(halo, y - rOut * 1.7);
            canvas.Children.Add(halo);
            if (_animate) Pulse(halo);

            var core = new Ellipse
            {
                Width = rOut * 0.9, Height = rOut * 0.9, Fill = new SolidColorBrush(Here),
                Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 14, Color = Here, Opacity = 0.85 },
            };
            Canvas.SetLeft(core, x - rOut * 0.45);
            Canvas.SetTop(core, y - rOut * 0.45);
            canvas.Children.Add(core);
        }

        if (st.Selected)
        {
            double fr = rOut + 6 * s;
            var focusRing = new Ellipse { Width = fr * 2, Height = fr * 2, Stroke = new SolidColorBrush(Focus), StrokeThickness = 2 * s };
            Canvas.SetLeft(focusRing, x - fr);
            Canvas.SetTop(focusRing, y - fr);
            canvas.Children.Add(focusRing);
        }
    }

    private static void AddChip(Canvas canvas, SceneCell st, Color lineColour, double x, double y, double rOut, double s)
    {
        bool empty = st.WindowCount == 0;
        bool marked = st.Selected || st.Here;

        Color fill = st.Selected ? Focus : st.Here ? Here : ScenePaint.Lerp(ChipBase, lineColour, 0.13);
        Color textColour = marked ? ChipInk : ScenePaint.Lerp(lineColour, ChipBase, empty ? 0.64 : 0.48);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = st.Label, FontFamily = Sans, FontSize = 12.5 * s,
            FontWeight = marked ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(textColour), VerticalAlignment = VerticalAlignment.Center,
        });
        if (!empty)
            content.Children.Add(new TextBlock
            {
                Text = st.WindowCount.ToString(), FontFamily = Mono, FontSize = 10 * s, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(marked ? Color.FromArgb(0xB4, ChipInk.R, ChipInk.G, ChipInk.B)
                                                        : ScenePaint.Lerp(lineColour, ChipBase, 0.58)),
                VerticalAlignment = VerticalAlignment.Center,
            });

        var chip = new Border
        {
            Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(9 * s),
            Padding = new Thickness(9 * s, 3 * s), Child = content,
        };
        if (!marked)
        {
            chip.BorderBrush = new SolidColorBrush(Dim(lineColour, empty ? 0.3 : 0.44));
            chip.BorderThickness = new Thickness(Math.Max(1, s));
        }
        chip.Measure(Size.Infinity);
        Canvas.SetLeft(chip, x - chip.DesiredSize.Width / 2);
        Canvas.SetTop(chip, y + rOut + 10 * s);
        canvas.Children.Add(chip);
    }

    // A resting route/station recedes toward the metro ground rather than going translucent over the live
    // desktop behind the overlay. Shares the arithmetic with the other themes via ScenePaint.
    private static Color Dim(Color c, double t) => ScenePaint.Toward(Bg, c, t);

    private static void AddRouteBadge(Canvas canvas, string name, Color colour, double x, double y, double s, double op)
    {
        var text = new TextBlock
        {
            Text = name, FontFamily = Mono, FontSize = 12 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Bg),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        double badgeTone = Math.Max(op, 0.72) * 0.8;
        var badge = new Border
        {
            Background = new SolidColorBrush(Dim(colour, badgeTone)),
            CornerRadius = new CornerRadius(11 * s), Padding = new Thickness(11 * s, 3 * s), Child = text,
        };
        badge.Measure(Size.Infinity);
        Canvas.SetLeft(badge, x);
        Canvas.SetTop(badge, y - badge.DesiredSize.Height / 2);
        canvas.Children.Add(badge);
    }

    // The "you are here" train breathes — a slow opacity pulse tied to the halo's visual-tree lifetime so it
    // self-stops on re-render. Best-effort: a flourish, never load-bearing.
    private static void Pulse(Control halo)
    {
        try
        {
            const double lo = 0.5, hi = 1.0, periodS = 1.6, tickS = 0.033;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tickS) };
            double t = 0;
            timer.Tick += (_, _) =>
            {
                t += tickS;
                double phase = (Math.Sin(t / periodS * 2 * Math.PI - Math.PI / 2) + 1) / 2;
                halo.Opacity = lo + (hi - lo) * phase;
            };
            halo.AttachedToVisualTree += (_, _) => timer.Start();
            halo.DetachedFromVisualTree += (_, _) => timer.Stop();
        }
        catch { /* a flourish, never load-bearing */ }
    }
}
