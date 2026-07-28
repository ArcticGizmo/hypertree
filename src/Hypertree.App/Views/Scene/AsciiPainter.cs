using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hypertree.Layout;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The terminal theme as an <see cref="IScenePainter"/>: every desktop is a monospace box-drawing card,
/// timelines are labelled rows joined by an ASCII spine, and the desktop you're on carries a blinking block
/// cursor. Pure fun — and a proof that a whole new look is just a painter: it owns no layout or camera logic,
/// so it stacks, aligns and pans exactly like the board and metro themes. See docs/design/scene-camera.md.
/// </summary>
internal sealed class AsciiPainter : IScenePainter
{
    private static readonly Color Ground = Color.Parse("#0C0F16"); // opaque card fill: masks the spine, lifts contrast
    private static readonly Color MainLine = Color.Parse("#C5D0E0");
    private static readonly Color SpineColour = Color.Parse("#3A4453");
    private static readonly Color Focus = Color.Parse("#6EA8FF");
    private static readonly Color Here = Color.Parse("#34D399");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private static readonly Color[] Palette =
    {
        Color.Parse("#F4795B"), Color.Parse("#5BC8F4"), Color.Parse("#7BD88F"), Color.Parse("#C99BF4"),
        Color.Parse("#F4C95B"), Color.Parse("#F45B9C"), Color.Parse("#63D6C4"), Color.Parse("#9CB2F4"),
    };

    private static Color BranchColour(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

    private const double FontSize = 15;
    private const int InnerW = 13;          // characters between the vertical box borders
    private const double BaseCellW = 138, BaseCellH = 60, BaseStride = 162, BaseRowPitch = 128, BaseRowH = 104, BaseLabelH = 20;

    private readonly bool _animate;
    public AsciiPainter(bool animate = false) => _animate = animate;

    public SceneMetrics Metrics(double s) => new(
        CellStride: BaseStride * s, CellWidth: BaseCellW * s, CellHeight: BaseCellH * s,
        RowPitch: BaseRowPitch * s, RowHeight: BaseRowH * s);

    // The label sits above the card, not out to the side, so there's no trailing handle beyond the cells.
    public double RowTrailing(SceneRow row, double s) => 0;

    public void PaintSpine(Canvas canvas, IReadOnlyList<(double X, double Y)> col0Centres, double s)
    {
        if (col0Centres.Count < 2) return;
        double top = col0Centres[0].Y, bottom = col0Centres[^1].Y, x = col0Centres[0].X;

        // A run of "│" from the first row to the last, drawn behind the (opaque) cards so it only shows in the
        // gaps between rows — a real ASCII connector rather than a drawn rectangle.
        var probe = new TextBlock { Text = "│", FontFamily = Mono, FontSize = FontSize * s };
        probe.Measure(Size.Infinity);
        double lh = probe.DesiredSize.Height > 0 ? probe.DesiredSize.Height : FontSize * 1.3 * s;
        int n = Math.Max(2, (int)Math.Round((bottom - top) / lh) + 1);

        var spine = new TextBlock
        {
            Text = string.Join("\n", System.Linq.Enumerable.Repeat("│", n)),
            FontFamily = Mono, FontSize = FontSize * s, Foreground = new SolidColorBrush(SpineColour),
            TextAlignment = TextAlignment.Center,
        };
        spine.Measure(Size.Infinity);
        Canvas.SetLeft(spine, x - spine.DesiredSize.Width / 2);
        Canvas.SetTop(spine, top - lh / 2);
        canvas.Children.Add(spine);
    }

    public void PaintRow(Canvas canvas, RowFrame frame, double s,
                         Action<int>? onClick, Action<int>? onActivate, Action<int>? onDelete)
    {
        IReadOnlyList<Rect> cells = frame.Cells;
        if (cells.Count == 0) return;

        Color rowColour = frame.Row.IsMain ? MainLine : BranchColour(frame.Row.BranchIndex);
        if (!frame.Row.Active) rowColour = Dim(rowColour, 0.55); // a resting timeline recedes (by colour, not opacity)

        // Row label above the first card: "» main" or "● name", on its own opaque ground so the spine doesn't
        // strike through it.
        string labelText = (frame.Row.IsMain ? "» " : "● ") + frame.Row.Name;
        var label = new TextBlock
        {
            Text = labelText, FontFamily = Mono, FontSize = FontSize * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(frame.Row.IsMain ? (frame.Row.Active ? Focus : Dim(MainLine, 0.6)) : rowColour),
            Background = new SolidColorBrush(Ground), Padding = new Thickness(3 * s, 0),
        };
        Canvas.SetLeft(label, cells[0].X);
        Canvas.SetTop(label, frame.CentreY - BaseCellH / 2 * s - BaseLabelH * s);
        canvas.Children.Add(label);

        for (int c = 0; c < cells.Count; c++)
        {
            int col = c;
            SceneCell cell = frame.Row.Cells[c];
            Color colour = cell.Selected ? Focus : cell.Here ? Here : (cell.WindowCount == 0 ? Dim(rowColour, 0.5) : rowColour);

            var card = new TextBlock
            {
                Text = BuildCard(cell, doubled: cell.Selected),
                FontFamily = Mono, FontSize = FontSize * s,
                Foreground = new SolidColorBrush(colour), Background = new SolidColorBrush(Ground),
                LineHeight = FontSize * 1.28 * s,
            };
            Canvas.SetLeft(card, cells[c].X);
            Canvas.SetTop(card, cells[c].Y);
            canvas.Children.Add(card);

            if (cell.Here) AddCursor(canvas, cells[c], s);

            if (onClick is not null || onActivate is not null)
            {
                var hit = new Border
                {
                    Width = cells[c].Width, Height = cells[c].Height, Background = Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                hit.PointerPressed += (_, e) =>
                {
                    if (e.ClickCount >= 2) onActivate?.Invoke(col);
                    else onClick?.Invoke(col);
                };
                Canvas.SetLeft(hit, cells[c].X);
                Canvas.SetTop(hit, cells[c].Y);
                canvas.Children.Add(hit);
            }
        }
    }

    // A three-line monospace card:  ┌─ label ─┐ / │ ### 4 │ / └────────┘  (double-lined when selected).
    private static string BuildCard(SceneCell cell, bool doubled)
    {
        (char tl, char tr, char bl, char br, char h, char v) = doubled
            ? ('╔', '╗', '╚', '╝', '═', '║')
            : ('┌', '┐', '└', '┘', '─', '│');

        string title = Trunc((cell.Here ? "@ " : "") + cell.Label, InnerW - 2);
        string topRaw = h + " " + title + " ";
        string top = topRaw.Length >= InnerW ? topRaw.Substring(0, InnerW) : topRaw + new string(h, InnerW - topRaw.Length);

        string wins = cell.WindowCount == 0 ? "" : new string('#', Math.Min(cell.WindowCount, InnerW - 4));
        string count = cell.WindowCount.ToString();
        string mid = (" " + wins);
        int padTo = InnerW - count.Length - 1;
        mid = (mid.Length > padTo ? mid.Substring(0, Math.Max(0, padTo)) : mid).PadRight(padTo) + count + " ";
        if (mid.Length > InnerW) mid = mid.Substring(0, InnerW);

        string line1 = tl + top + tr;
        string line2 = v + mid + v;
        string line3 = bl + new string(h, InnerW) + br;
        return line1 + "\n" + line2 + "\n" + line3;
    }

    // The terminal "you are here": a block cursor at the card's top-right that blinks (live overlay only).
    private void AddCursor(Canvas canvas, Rect cellRect, double s)
    {
        var cursor = new TextBlock
        {
            Text = "█", FontFamily = Mono, FontSize = FontSize * s, Foreground = new SolidColorBrush(Here),
        };
        Canvas.SetLeft(cursor, cellRect.Right - 16 * s);
        Canvas.SetTop(cursor, cellRect.Y + 2 * s);
        canvas.Children.Add(cursor);
        if (_animate) Blink(cursor);
    }

    private static string Trunc(string text, int max)
        => max <= 0 ? "" : text.Length <= max ? text : text.Substring(0, max);

    // Opaque dim toward the ground, so a resting timeline recedes without going translucent over the live
    // desktop behind the overlay (mirrors the metro theme's colour-based dimming).
    private static Color Dim(Color c, double t)
    {
        byte M(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
        return Color.FromArgb(0xFF, M(Ground.R, c.R), M(Ground.G, c.G), M(Ground.B, c.B));
    }

    // A hard on/off blink — a terminal cursor, not a fade. Tied to the visual-tree lifetime so it self-stops
    // on re-render; best-effort, never load-bearing (mirrors MetroPainter.Pulse).
    private static void Blink(Control cursor)
    {
        try
        {
            const double periodS = 1.0, tickS = 0.1;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tickS) };
            double t = 0;
            timer.Tick += (_, _) => { t += tickS; cursor.Opacity = (t % periodS) < periodS / 2 ? 1 : 0; };
            cursor.AttachedToVisualTree += (_, _) => timer.Start();
            cursor.DetachedFromVisualTree += (_, _) => timer.Stop();
        }
        catch { /* a flourish, never load-bearing */ }
    }
}
