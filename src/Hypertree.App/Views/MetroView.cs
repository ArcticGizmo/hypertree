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
/// The spatial model matches the board so the two are interchangeable: lines stack in the same order
/// (branches before <see cref="NavMap.TopPosition"/> above main, the rest below) and each line is centred on
/// its own cursor so the current station sits on the centre column (which keeps the trunk straight). It
/// differs in one deliberate way — the board pins the <em>current</em> row to the screen centre, whereas the
/// metro view centres the whole stack, reading as an overview you locate yourself within. Blue marks the
/// selection/target; a green "you are here" marker (the train) marks the desktop you're actually on.
/// </summary>
internal static class MetroView
{
    private static readonly Color Bg = Color.Parse("#0F131B");
    private static readonly Color Trunk = Color.Parse("#3A4453");
    private static readonly Color MainLine = Color.Parse("#C5D0E0");
    private static readonly Color ChipBase = Color.Parse("#111722"); // opaque ground for a resting desktop chip
    private static readonly Color ChipInk = Color.Parse("#0A0D12");   // dark text on a bright (selected/here) chip
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
    /// <param name="layout">When supplied, filled with each station's cell and each line's band in the same
    /// scheme <see cref="BoardView"/> uses — so the interactive map's click and drag code hit-tests the metro
    /// diagram with no changes. A line's route badge is its branch drag handle (stations tile the strip).</param>
    public static Control Render(NavMap map, double screenW, double screenH, double s = 1.0, bool animate = false,
                                 Action<int>? onTopClick = null, Action<int, int>? onBranchClick = null,
                                 Action<int>? onTopActivate = null, Action<int, int>? onBranchActivate = null,
                                 BoardLayout? layout = null)
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
            Line ln = lines[i];
            double op = ln.Active ? 1.0 : ln.IsMain ? 0.82 : 0.5;
            Action<int>? click = ln.IsMain ? onTopClick
                                : onBranchClick is null ? null : j => onBranchClick(ln.BranchIndex, j);
            Action<int>? activate = ln.IsMain ? onTopActivate
                                : onBranchActivate is null ? null : j => onBranchActivate(ln.BranchIndex, j);
            DrawLine(canvas, ln, bx, y[i], stride, lineW, rOut, s, op, animate, click, activate, layout);
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
    private sealed record Line(string Name, Color Colour, bool IsMain, int BranchIndex, bool Active, int Cursor, IReadOnlyList<Station> Stations);

    private static Line MainLineOf(NavMap map)
        => new("main", MainLine, IsMain: true, BranchIndex: -1, Active: map.OnTop, map.TopCursor,
               map.TopRow.Select(t => new Station(t.Label, map.OnTop && t.IsCurrent, t.IsHere, t.WindowCount)).ToList());

    private static Line BranchLine(NavMapBranch g)
        => new(g.Name, BranchColour(g.Index), IsMain: false, g.Index, Active: g.IsCurrentLevel, g.Cursor,
               g.Desktops.Select(d => new Station(d.Label, d.IsCurrent, d.IsHere, d.WindowCount)).ToList());

    private static void DrawLine(Canvas canvas, Line line, double cx, double y, double stride,
                                 double lineW, double rOut, double s, double op, bool animate,
                                 Action<int>? onClick, Action<int>? onActivate, BoardLayout? layout)
    {
        int n = line.Stations.Count;
        double originX = cx - line.Cursor * stride; // the cursor station lands on cx

        double firstX = originX, lastX = originX + (n - 1) * stride;
        var routeBrush = new SolidColorBrush(Dim(line.Colour, op)); // opaque dim — a resting line stays visible over anything behind

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
        double badgeLeft = routeRight + 16 * s;
        AddRouteBadge(canvas, line, badgeLeft, y, s, op);

        // Report geometry for the interactive map. Station "cells" tile the strip (each stride wide, centred
        // on its station), so a press on a station is a desktop drag; the leftover band out to the badge is
        // the branch drag handle (see the class doc). Boundaries land on the mid-points between stations.
        // The cell spans the on-line donut down through the label chip below it.
        double cellTop = y - rOut - 10 * s, cellH = 2 * rOut + 48 * s;
        if (layout is not null)
        {
            double bandLeft = originX - stride / 2;
            double bandRight = badgeLeft + EstimateBadgeWidth(line.Name, s);
            layout.Add(new BoardRow(new Rect(bandLeft, cellTop, bandRight - bandLeft, cellH),
                                    line.IsMain, line.BranchIndex, n,
                                    FirstTileLeft: originX, TileStride: stride, TileGap: stride,
                                    TileTop: cellTop, TileHeight: cellH));
        }

        // Stations along the line.
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            double sx = originX + i * stride;
            AddStation(canvas, line.Stations[i], line.Colour, sx, y, rOut, s, op, animate);
            AddChip(canvas, line.Stations[i], line.Colour, sx, y, rOut, s);

            if (layout is not null)
                layout.Add(new BoardTile(new Rect(sx - stride / 2, cellTop, stride, cellH),
                                         line.IsMain, line.BranchIndex, i));

            // A transparent cell on top carries the click: single = select (onClick), double = switch
            // (onActivate). It must NOT mark the press handled, so it bubbles to the map, which reads it as
            // "a tile was pressed" and may turn it into a drag — mirrors BoardView's tile handler exactly.
            if (onClick is not null || onActivate is not null)
            {
                var hit = new Border
                {
                    Width = stride, Height = cellH, Background = Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                hit.PointerPressed += (_, e) =>
                {
                    if (e.ClickCount >= 2) onActivate?.Invoke(idx);
                    else onClick?.Invoke(idx);
                };
                Canvas.SetLeft(hit, sx - stride / 2);
                Canvas.SetTop(hit, cellTop);
                canvas.Children.Add(hit);
            }
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

        Color donut = st.Focused ? Focus : st.Here ? Here : Dim(lineColour, op);
        var dot = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = new SolidColorBrush(Bg),
            Stroke = new SolidColorBrush(donut),
            StrokeThickness = ring,
        };
        Canvas.SetLeft(dot, x - r);
        Canvas.SetTop(dot, y - r);
        canvas.Children.Add(dot);

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

    // Each desktop is a chip below its station — the same rounded pill as the branch route badge, so the
    // label always sits on an opaque ground and stays legible over whatever desktop shows through the
    // semi-transparent overlay. Resting chips are dimmed (a dark, line-tinted fill with the line's colour as
    // the text); the selected desktop (blue) and the one you're actually on (green) invert to a bright solid
    // fill, mirroring the branch marker. The window count rides along inside the chip, so it's on the opaque
    // ground too.
    private static void AddChip(Canvas canvas, Station st, Color lineColour, double x, double y, double rOut, double s)
    {
        bool empty = st.WindowCount == 0;
        bool marked = st.Focused || st.Here;

        // Resting chips are held well back so the bright selected/here chips carry the eye: the text is
        // darkened most of the way toward the chip base (a hint of the line hue remains) and the count is
        // dimmer still. The fill stays opaque, so "quiet" never means "hard to read".
        Color fill = st.Focused ? Focus : st.Here ? Here : Lerp(ChipBase, lineColour, 0.13);
        Color textColour = marked ? ChipInk : Lerp(lineColour, ChipBase, empty ? 0.64 : 0.48);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6 * s, VerticalAlignment = VerticalAlignment.Center,
        };
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
                                                        : Lerp(lineColour, ChipBase, 0.58)),
                VerticalAlignment = VerticalAlignment.Center,
            });

        var chip = new Border
        {
            Background = new SolidColorBrush(fill),
            CornerRadius = new CornerRadius(9 * s), Padding = new Thickness(9 * s, 3 * s), Child = content,
        };
        if (!marked) // a faint coloured edge just defines the chip on varied backdrops, without pulling focus
        {
            chip.BorderBrush = new SolidColorBrush(Dim(lineColour, empty ? 0.3 : 0.44)); // opaque dim, not translucent
            chip.BorderThickness = new Thickness(Math.Max(1, s));
        }
        chip.Measure(Size.Infinity);
        Canvas.SetLeft(chip, x - chip.DesiredSize.Width / 2);
        Canvas.SetTop(chip, y + rOut + 10 * s);
        canvas.Children.Add(chip);
    }

    // Opaque blend from a→b by t, for the dark, line-tinted resting chip fill and its muted text.
    private static Color Lerp(Color a, Color b, double t)
    {
        byte M(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
        return Color.FromArgb(0xFF, M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    // Dim a colour by fading it toward the overlay ground rather than lowering its opacity — the result is
    // opaque, so a dimmed line, station or badge keeps its contrast over a busy desktop instead of going
    // translucent and getting lost in it. t = 1 is the full colour; smaller t recedes toward the ground.
    private static Color Dim(Color c, double t) => Lerp(Bg, c, t);

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
            // Opaque, and never so dim the dark text stops reading — a resting branch's name label recedes a
            // little but stays crisp rather than washing out against the desktop behind.
            Background = new SolidColorBrush(Dim(line.Colour, Math.Max(op, 0.72))),
            CornerRadius = new CornerRadius(11 * s), Padding = new Thickness(11 * s, 3 * s),
            Child = text,
        };
        badge.Measure(Size.Infinity);
        Canvas.SetLeft(badge, x);
        Canvas.SetTop(badge, y - badge.DesiredSize.Height / 2);
        canvas.Children.Add(badge);
    }
}
