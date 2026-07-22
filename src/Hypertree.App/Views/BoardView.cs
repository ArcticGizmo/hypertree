using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// Renders the Model-P board exactly like docs/design/p-vs-q.html: a day-to-day row of desktop
/// "tiles" (a screen with mini-window bars + a caption), and the current anchor's scope as an amber
/// box hanging beneath it — greyed/resting when you're on the top row, lit when you've dived in. The
/// current desktop is outlined (focus ring) and the whole board is translated so that tile stays
/// centred, so navigating doesn't make the layout jump around. Shared by the transient flash
/// (<see cref="HudWindow"/>) and the interactive overlay (<see cref="MapOverlay"/>).
/// </summary>
internal static class BoardView
{
    // Dark palette — the p-vs-q.html dark theme values.
    private static readonly Color TileBg = Color.Parse("#1F2836"), TileBorder = Color.Parse("#2A3444"), TileWin = Color.Parse("#374357");
    private static readonly Color StrBg = Color.Parse("#3A2E18"), StrBorder = Color.Parse("#6A5124"), StrWin = Color.Parse("#C9922F"), StrInk = Color.Parse("#E8A23D");
    private static readonly Color CapBg = Color.Parse("#161C27"), Ink = Color.Parse("#E8EDF5"), InkSoft = Color.Parse("#9AA6B8"), InkFaint = Color.Parse("#69748A");
    private static readonly Color Focus = Color.Parse("#6EA8FF");
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    public static Control Render(NavMap map, double s = 1.0)
    {
        double tileW = 96 * s, screenH = 50 * s, capH = 22 * s, gap = 14 * s, conn = 22 * s;
        double tileH = screenH + capH, lift = 6 * s, top = 14 * s, pad = 22 * s;
        double scopePad = 10 * s, labelH = 22 * s;

        int n = map.Anchors.Count;
        int cur = 0;
        for (int i = 0; i < n; i++) if (map.Anchors[i].IsCurrentColumn) cur = i;

        double Ax(int i) => i * (tileW + gap);
        double rowY = top + lift; // leave room above for the focused tile to lift into

        bool hasScope = map.ScopeDesktops is not null;
        int scopeCur = -1;
        double scopeBoxX = 0, scopeBoxY = 0, scopeBoxW = 0, scopeBoxH = 0;
        if (hasScope)
        {
            var sd = map.ScopeDesktops!;
            for (int j = 0; j < sd.Count; j++) if (sd[j].IsCurrent) scopeCur = j;
            scopeBoxX = Ax(cur) - scopePad;
            scopeBoxY = rowY + tileH + conn;
            scopeBoxW = scopePad * 2 + sd.Count * tileW + (sd.Count - 1) * gap;
            scopeBoxH = labelH + lift + tileH + scopePad * 2;
        }

        // The tile that must stay centred: the current scope desktop when dived, else the anchor.
        double fx = (map.InScope && hasScope && scopeCur >= 0)
            ? Ax(cur) + scopeCur * (tileW + gap) + tileW / 2
            : Ax(cur) + tileW / 2;

        double viewportW = Math.Max((tileW + gap) * 5 + pad * 2, (hasScope ? scopeBoxW : 0) + pad * 2);
        double offsetX = viewportW / 2 - fx;
        double viewportH = (hasScope ? scopeBoxY + scopeBoxH : rowY + tileH) + pad;

        var canvas = new Canvas { Width = viewportW, Height = viewportH, ClipToBounds = true };

        if (hasScope)
        {
            // Connector line from the anchor down into the scope box.
            var line = new Rectangle { Width = Math.Max(2, 2 * s), Height = conn, Fill = new SolidColorBrush(StrBorder) };
            Canvas.SetLeft(line, Ax(cur) + tileW / 2 - 1 + offsetX);
            Canvas.SetTop(line, rowY + tileH);
            canvas.Children.Add(line);

            Control box = BuildScopeBox(map, s, tileW, screenH, capH, gap, scopePad, lift);
            box.Opacity = map.InScope ? 1.0 : 0.42; // resting = greyed/dimmed until you dive in
            Canvas.SetLeft(box, scopeBoxX + offsetX);
            Canvas.SetTop(box, scopeBoxY);
            canvas.Children.Add(box);
        }

        for (int i = 0; i < n; i++)
        {
            NavMapAnchor a = map.Anchors[i];
            bool focused = a.IsCurrentColumn && !map.InScope;
            Control tile = Tile(a.Label, isStream: false, focused, s, tileW, screenH, capH);
            Canvas.SetLeft(tile, Ax(i) + offsetX);
            Canvas.SetTop(tile, rowY);
            canvas.Children.Add(tile);
        }

        return canvas;
    }

    private static Control BuildScopeBox(NavMap map, double s, double tileW, double screenH, double capH,
                                         double gap, double scopePad, double lift)
    {
        var label = new TextBlock
        {
            Text = "● " + map.ScopeName + (map.InScope ? "" : "  · resting"),
            FontFamily = Mono, FontSize = 11 * s, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(StrInk), Margin = new Thickness(2 * s, 0, 0, 6 * s),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap };
        var sd = map.ScopeDesktops!;
        for (int j = 0; j < sd.Count; j++)
            row.Children.Add(Tile(sd[j].Label, isStream: true, sd[j].IsCurrent && map.InScope, s, tileW, screenH, capH));

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
                                double tileW, double screenH, double capH)
    {
        Color border = focused ? Focus : (isStream ? StrBorder : TileBorder);
        double bt = focused ? 2 : 1;

        // Screen with mini windows.
        var winCanvas = new Canvas { Width = tileW, Height = screenH };
        Color win = isStream ? StrWin : TileWin;
        AddWin(winCanvas, 9 * s, 9 * s, 44 * s, 14 * s, win, 1.0);
        AddWin(winCanvas, 9 * s, 27 * s, 30 * s, 13 * s, win, 1.0);
        AddWin(winCanvas, tileW - 9 * s - 22 * s, 14 * s, 22 * s, 26 * s, win, 0.7);

        var screen = new Border
        {
            Width = tileW, Height = screenH,
            Background = new SolidColorBrush(isStream ? StrBg : TileBg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(bt, bt, bt, 0),
            CornerRadius = new CornerRadius(8 * s, 8 * s, 0, 0),
            ClipToBounds = true,
            Child = winCanvas,
        };

        var cap = new Border
        {
            Width = tileW, Height = capH,
            Background = new SolidColorBrush(CapBg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(bt, 0, bt, bt),
            CornerRadius = new CornerRadius(0, 0, 8 * s, 8 * s),
            Child = new TextBlock
            {
                Text = caption, FontFamily = Mono, FontSize = 11 * s,
                Foreground = new SolidColorBrush(focused ? Ink : (isStream ? StrInk : InkSoft)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical, Width = tileW, Children = { screen, cap } };
        if (focused) stack.RenderTransform = new TranslateTransform(0, -6 * s); // lift the current desktop
        return stack;
    }

    private static void AddWin(Canvas c, double x, double y, double w, double h, Color color, double opacity)
    {
        var r = new Rectangle
        {
            Width = w, Height = h, RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(color), Opacity = opacity,
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }
}
