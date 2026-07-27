using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// A transit-diagram rendering of the same <see cref="NavMap"/> the <see cref="BoardView"/> draws — the
/// "metro map" view. Each timeline (main and every branch) is a coloured <b>line</b> running horizontally;
/// each desktop is a <b>station</b> on it; a neutral vertical <b>interchange trunk</b> at screen-centre ties
/// the lines together along the dive/surface axis, exactly where <see cref="BoardView"/> draws its spine.
///
/// The spatial model is identical to the board so the two are interchangeable: rows stack in the same order
/// (branches before <see cref="NavMap.TopPosition"/> above main, the rest below), each line is centred on its
/// own cursor so the current station sits on the centre column, and the whole stack scrolls vertically so the
/// current line lands on the screen's middle. Blue marks the selection/target, a green "you are here" marker
/// (the train) marks the desktop you're actually on.
/// </summary>
internal static class MetroView
{
    private static readonly Color Bg = Color.Parse("#0F131B");
    private static readonly Color Trunk = Color.Parse("#3A4453");
    private static readonly Color MainLine = Color.Parse("#C5D0E0");
    private static readonly Color Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8"), InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Color.Parse("#6EA8FF");
    private static readonly Color Here = Color.Parse("#34D399");
    private static readonly FontFamily Sans = new("Inter,Segoe UI,sans-serif");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // A metro palette for branch lines — saturated, distinguishable on the dark ground, assigned by branch
    // index (wrapping if there are more branches than colours).
    private static readonly Color[] LinePalette =
    {
        Color.Parse("#F4795B"), // coral
        Color.Parse("#5BC8F4"), // sky
        Color.Parse("#7BD88F"), // green
        Color.Parse("#C99BF4"), // violet
        Color.Parse("#F4C95B"), // amber
        Color.Parse("#F45B9C"), // magenta
        Color.Parse("#63D6C4"), // teal
        Color.Parse("#9CB2F4"), // periwinkle
    };

    private static Color BranchColour(int branchIndex) => LinePalette[((branchIndex % LinePalette.Length) + LinePalette.Length) % LinePalette.Length];

    /// <param name="animate">When true (the live overlay), the "you are here" train gently pulses. The
    /// offscreen shot path leaves it false so a capture is a single settled frame.</param>
    public static Control Render(NavMap map, double screenW, double screenH, double s = 1.0, bool animate = false)
    {
        double stride = 156 * s;   // spacing between stations along a line
        double lineW = 8 * s;      // route stroke width
        double rOut = 10 * s;      // station donut outer radius
        double vgap = 140 * s;     // vertical spacing between lines
        double cx = screenW / 2, cy = screenH / 2;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // ── Ordered lines: branches above main, main, branches below (same sequence as BoardView). ──
        int split = Math.Clamp(map.TopPosition, 0, map.Branches.Count);
        var lines = new List<Line>();
        for (int gi = 0; gi < split; gi++) lines.Add(BranchLine(map.Branches[gi]));
        lines.Add(MainLineOf(map));
        for (int gi = split; gi < map.Branches.Count; gi++) lines.Add(BranchLine(map.Branches[gi]));

        // Stack the lines top→bottom on a fixed pitch, centred as a whole on cy. (The board pins the *current*
        // row to centre and scrolls the rest; the metro view is an overview, so a balanced composition reads
        // better — you find yourself by the green train, not by a fixed centre line.)
        var y = new double[lines.Count];
        double stackH = (lines.Count - 1) * vgap;
        for (int i = 0; i < lines.Count; i++) y[i] = cy - stackH / 2 + i * vgap;

        // Lines centre horizontally on their cursor (so the trunk is straight through cx), but the route
        // badges hang off the right — which pulls the whole picture right of centre. Measure how far the
        // content reaches either side of cx and slide the origin so the composition sits balanced.
        double leftReach = 0, rightReach = 0;
        foreach (Line ln in lines)
        {
            int n = ln.Stations.Count;
            int cur = Math.Clamp(ln.Cursor, 0, Math.Max(0, n - 1));
            leftReach = Math.Max(leftReach, cur * stride + rOut);
            double routeRight = n <= 1 ? stride * 0.32 : (n - 1 - cur) * stride;
            rightReach = Math.Max(rightReach, routeRight + rOut + 16 * s + EstimateBadgeWidth(ln.Name, s));
        }
        double bx = cx + (leftReach - rightReach) / 2; // balanced origin: the "cx" every line is built around

        // ── The interchange trunk: a neutral vertical tie through cx joining every line's centre station,
        // the dive/surface axis. Drawn first, so the coloured routes and station donuts sit over it. ──
        if (lines.Count > 1)
        {
            var tie = new Rectangle
            {
                Width = Math.Max(2, 3 * s), Height = y[^1] - y[0], RadiusX = 1.5 * s, RadiusY = 1.5 * s,
                Fill = new SolidColorBrush(Trunk),
            };
            Canvas.SetLeft(tie, bx - 1.5 * s);
            Canvas.SetTop(tie, y[0]);
            canvas.Children.Add(tie);
        }

        // ── Each line: the coloured route, its stations, labels, and a route badge at the terminus. ──
        // Resting lines recede so the one you're on reads first — but main only half-fades (it stays the
        // recognisable home spine even from inside a branch), where a resting branch fades further.
        for (int i = 0; i < lines.Count; i++)
        {
            double op = lines[i].Active ? 1.0 : lines[i].IsMain ? 0.82 : 0.5;
            DrawLine(canvas, lines[i], bx, y[i], stride, lineW, rOut, s, op, animate);
        }

        return canvas;
    }

    // Rough width of a route badge, for balancing the composition before anything is drawn. The mono glyphs
    // are near-fixed-width, so char-count × advance + padding is close enough to centre by.
    private static double EstimateBadgeWidth(string name, double s)
        => name.Length * 7.4 * s + 22 * s;

    // The train "breathes": a slow opacity pulse on the here-halo, so your position is alive on the map. Built
    // as a DispatcherTimer tween to match the app's hand-rolled animation style (HudWindow), and tied to the
    // halo's visual-tree lifetime so it starts when the map shows and stops the instant a re-render or close
    // detaches it — no leaked timers ticking on orphaned controls. Best-effort: if anything throws, the halo
    // just sits static rather than taking the map down with it.
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
                double phase = (Math.Sin(t / periodS * 2 * Math.PI - Math.PI / 2) + 1) / 2; // 0→1→0, eased
                halo.Opacity = lo + (hi - lo) * phase;
            };
            halo.AttachedToVisualTree += (_, _) => timer.Start();
            halo.DetachedFromVisualTree += (_, _) => timer.Stop();
        }
        catch { /* a flourish, never load-bearing */ }
    }

    private sealed record Station(string Label, bool Focused, bool Here, int WindowCount);
    private sealed record Line(string Name, Color Colour, bool IsMain, bool Active, int Cursor, IReadOnlyList<Station> Stations);

    private static Line MainLineOf(NavMap map)
        => new("main", MainLine, IsMain: true, Active: map.OnTop, map.TopCursor,
               map.TopRow.Select(t => new Station(t.Label, map.OnTop && t.IsCurrent, t.IsHere, t.WindowCount)).ToList());

    private static Line BranchLine(NavMapBranch g)
        => new(g.Name, BranchColour(g.Index), IsMain: false, Active: g.IsCurrentLevel, g.Cursor,
               g.Desktops.Select(d => new Station(d.Label, d.IsCurrent, d.IsHere, d.WindowCount)).ToList());

    private static void DrawLine(Canvas canvas, Line line, double cx, double y, double stride,
                                 double lineW, double rOut, double s, double op, bool animate)
    {
        int n = line.Stations.Count;
        double originX = cx - line.Cursor * stride; // the cursor station lands on cx

        double firstX = originX, lastX = originX + (n - 1) * stride;
        var routeBrush = new SolidColorBrush(line.Colour) { Opacity = op };

        // The route: a single rounded horizontal stroke spanning the stations. A one-station line still
        // gets a short stub so the colour and cap read.
        double routeLeft = n <= 1 ? firstX - stride * 0.32 : firstX;
        double routeRight = n <= 1 ? firstX + stride * 0.32 : lastX;
        var route = new Rectangle
        {
            Width = routeRight - routeLeft, Height = lineW, RadiusX = lineW / 2, RadiusY = lineW / 2,
            Fill = routeBrush,
        };
        // The line you're on gets a soft coloured glow, so it reads as lit while the resting lines lie flat.
        if (op >= 1.0)
            route.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 16, Color = line.Colour, Opacity = 0.5 };
        Canvas.SetLeft(route, routeLeft);
        Canvas.SetTop(route, y - lineW / 2);
        canvas.Children.Add(route);

        // Route badge (the line name) just past the terminus, in the line's colour.
        AddRouteBadge(canvas, line, routeRight + 16 * s, y, s, op);

        // Stations along the line.
        for (int i = 0; i < n; i++)
        {
            double sx = originX + i * stride;
            AddStation(canvas, line.Stations[i], line.Colour, sx, y, rOut, s, op, animate);
            AddStationLabel(canvas, line.Stations[i], sx, y, rOut, s, op);
        }
    }

    // A station donut: a filled ring in the line colour with the background showing through the hole — the
    // classic transit tick. Focus (blue) and here (green) get their own treatment so target vs. actual-position
    // read at a glance. Empty desktops (no windows) read as a smaller, hollow "minor" station.
    private static void AddStation(Canvas canvas, Station st, Color lineColour, double x, double y,
                                   double rOut, double s, double op, bool animate)
    {
        bool empty = st.WindowCount == 0;
        bool marked = st.Focused || st.Here;
        double r = empty && !marked ? rOut * 0.66 : rOut;
        double ring = empty && !marked ? 2.5 * s : 3.5 * s;

        Color donut = st.Focused ? Focus : st.Here ? Here : lineColour;
        var dot = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = new SolidColorBrush(Bg),
            Stroke = new SolidColorBrush(donut) { Opacity = marked ? 1.0 : op },
            StrokeThickness = ring,
        };
        Canvas.SetLeft(dot, x - r);
        Canvas.SetTop(dot, y - r);
        canvas.Children.Add(dot);

        // Window count: a faint tally above an occupied station — the metro equivalent of the board's count
        // badge. Empty stations stay bare, which (with the smaller hollow donut) reads as "nothing here".
        if (!empty)
        {
            var count = new TextBlock
            {
                Text = st.WindowCount.ToString(), FontFamily = Mono, FontSize = 10 * s, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(st.Here ? Here : st.Focused ? Focus : InkFaint) { Opacity = marked ? 1.0 : op },
            };
            count.Measure(Size.Infinity);
            Canvas.SetLeft(count, x - count.DesiredSize.Width / 2);
            Canvas.SetTop(count, y - rOut - 15 * s);
            canvas.Children.Add(count);
        }

        // "You are here" — the train: a filled green core with a translucent halo, so the current desktop
        // reads as occupied, not just outlined.
        if (st.Here)
        {
            var halo = new Ellipse
            {
                Width = rOut * 3.4, Height = rOut * 3.4,
                Fill = new SolidColorBrush(Here) { Opacity = 0.18 },
            };
            Canvas.SetLeft(halo, x - rOut * 1.7);
            Canvas.SetTop(halo, y - rOut * 1.7);
            canvas.Children.Add(halo);
            if (animate) Pulse(halo);

            var core = new Ellipse
            {
                Width = rOut * 0.9, Height = rOut * 0.9, Fill = new SolidColorBrush(Here),
                Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = 14, Color = Here, Opacity = 0.85 },
            };
            Canvas.SetLeft(core, x - rOut * 0.45);
            Canvas.SetTop(core, y - rOut * 0.45);
            canvas.Children.Add(core);
        }

        // The focus/target station gets a blue outer ring — a selection halo distinct from the green train.
        if (st.Focused)
        {
            double fr = rOut + 6 * s;
            var focusRing = new Ellipse
            {
                Width = fr * 2, Height = fr * 2,
                Stroke = new SolidColorBrush(Focus), StrokeThickness = 2 * s,
            };
            Canvas.SetLeft(focusRing, x - fr);
            Canvas.SetTop(focusRing, y - fr);
            canvas.Children.Add(focusRing);
        }
    }

    private static void AddStationLabel(Canvas canvas, Station st, double x, double y, double rOut, double s, double op)
    {
        bool marked = st.Focused || st.Here;
        var label = new TextBlock
        {
            Text = st.Label, FontFamily = Sans, FontSize = 13 * s,
            FontWeight = marked ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(st.Focused ? Ink : st.Here ? Here : (st.WindowCount == 0 ? InkFaint : InkSoft))
                { Opacity = marked ? 1.0 : op },
        };
        label.Measure(Size.Infinity);
        Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, y + rOut + 12 * s);
        canvas.Children.Add(label);
    }

    private static void AddRouteBadge(Canvas canvas, Line line, double x, double y, double s, double op)
    {
        var text = new TextBlock
        {
            Text = line.Name, FontFamily = Mono, FontSize = 12 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Bg), // dark ink reads on every line colour
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var badge = new Border
        {
            Background = new SolidColorBrush(line.Colour) { Opacity = op },
            CornerRadius = new CornerRadius(11 * s), Padding = new Thickness(11 * s, 3 * s),
            Child = text,
        };
        badge.Measure(Size.Infinity);
        Canvas.SetLeft(badge, x);
        Canvas.SetTop(badge, y - badge.DesiredSize.Height / 2);
        canvas.Children.Add(badge);
    }
}
