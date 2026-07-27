using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hypertree.App.Views;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// Offscreen render of the board to PNG (invoked with <c>--shot &lt;dir&gt;</c>) — the standing way to
/// eyeball the visualization without a display, and without screenshotting the real desktop (which
/// would capture unrelated windows). Renders ONLY Hypertree's own synthetic board. Sample data mirrors
/// docs/design/p-vs-q.html so the output can be compared against the design directly.
/// </summary>
internal static class DesignShot
{
    public static void Capture(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // Window counts vary per tile (with one empty desktop, "Notes"=0) so the shot exercises the
        // at-a-glance count badges and the dimmed-empty styling. `here` marks the "came from" desktop
        // with the green outline shown while navigating.
        List<NavMapTile> Top(int current, int here = -1) => new()
        {
            new("Home", current == 0, here == 0, 4), new("Comms", current == 1, here == 1, 2),
            new("Web", current == 2, here == 2, 7), new("Notes", current == 3, here == 3, 0),
        };
        NavMapBranch Feat(bool live, int cur) => new(0, "FEAT-123", new List<NavMapTile>
        {
            new("SPA", live && cur == 0, WindowCount: 3), new("API", live && cur == 1, WindowCount: 1),
            new("Mobile", live && cur == 2, WindowCount: 0),
        }, live, cur);
        NavMapBranch Hotfix() => new(1, "hotfix", new List<NavMapTile>
            { new("db", false, WindowCount: 1), new("api", false, WindowCount: 0) }, false, 0);

        // Stable pivot: FEAT-123 sits above main, hotfix below (main slot 1). On the main timeline,
        // Web (cursor 2) is current and main renders between the two branches.
        Save(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-top-row.png"));

        // Same fixed layout, now with the cursor inside FEAT-123 (on API=cursor 1) — the branch above
        // main. Main keeps its slot; it does not move. We dived from Web, so it wears the green
        // "came from" outline.
        Save(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-dived.png"));

        // The drag geometry: the same board with the BoardLayout it reported drawn back over it. The map's
        // drag hit-tests that layout rather than the visual tree, so this is how we check the two agree —
        // every outline should sit exactly on the tile (or branch box) it claims, and each caret in the
        // middle of the gap it inserts at.
        SaveLayoutCheck(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
                        Path.Combine(outDir, "board-drag-layout.png"));

        // The metro-map view of the same two states, so it can be compared tile-for-station against the board.
        SaveMetro(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "metro-top-row.png"));
        SaveMetro(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "metro-dived.png"));

        SaveCards(outDir);
    }

    /// <summary>
    /// The two card windows, rendered at the exact size they open at.
    /// </summary>
    /// <remarks>
    /// Neither can be reached from here by any other means: both are opened from the tray menu or by a
    /// version change, so checking a layout tweak otherwise means installing a build and clicking through
    /// to it. They are also both <c>CanResize = false</c>, which makes their width a design decision
    /// rather than something the user can fix — worth being able to look at on demand.
    /// </remarks>
    private static void SaveCards(string outDir)
    {
        var activator = PlatformServices.CreateForegroundActivator();

        // Read the real embedded CHANGELOG so the shot shows genuine content at genuine lengths — made-up
        // bullets would not tell us whether real ones wrap.
        var markdown = ChangelogMarkdown.LoadEmbedded() ?? "";
        var sections = Changelog.ChangelogParser.Parse(markdown)
            .Where(s => s.Version is not null)
            .Take(2)
            .ToList();

        var changelog = new ChangelogWindow(
            "What's new in Hypertree", "Here's what changed across the last 2 releases.",
            sections, activator, onSuppress: () => { });
        SaveCard(changelog, Path.Combine(outDir, "card-changelog.png"));

        var settings = new SettingsWindow(new Settings.AppSettings(), startOnLogin: true,
                                          onSave: (_, _) => { }, activator);
        SaveCard(settings, Path.Combine(outDir, "card-settings.png"));
    }

    /// <summary>
    /// Render a card window at the size it actually opens at.
    /// </summary>
    /// <remarks>
    /// The window is shown, off the side of the screen, rather than having its content re-hosted in a
    /// detached container. Templated controls — every <c>Button</c> on these cards — only acquire a
    /// template once they are inside a styled window root, so a detached render silently drops them and
    /// produces a screenshot missing exactly the parts most worth looking at. Showing it for real also
    /// means the measured size is the true one, including whatever <c>SizeToContent</c> settles on.
    /// </remarks>
    private static void SaveCard(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-32000, -32000); // offscreen: no flash on the real desktop
        window.ShowInTaskbar = false;
        window.Show();

        // Let styles apply and layout settle before measuring or rendering. One pass isn't always enough:
        // applying a template can dirty layout again.
        for (int i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        int w = (int)Math.Ceiling(window.Bounds.Width);
        int h = (int)Math.Ceiling(window.Bounds.Height);
        if (w <= 0 || h <= 0) { window.Close(); return; }

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        using (var fs = File.Create(path)) rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({w}x{h})");

        window.Close();
    }

    // A representative primary-monitor size, so the shot shows the real full-screen, centred layout
    // (F1/F3) rather than a size-to-content card.
    private const int ScreenW = 1440, ScreenH = 900;

    // Render the board, then draw the layout it reported on top: a box per tile, a box per row band, and a
    // caret at every insertion point of every row.
    private static void SaveLayoutCheck(NavMap map, string path)
    {
        var layout = new BoardLayout();
        Control board = BoardView.Render(map, ScreenW, ScreenH, 1.0, layout: layout);

        var marks = new Canvas { Width = ScreenW, Height = ScreenH };
        void Outline(Rect r, Color c, double thickness)
        {
            var b = new Border
            {
                Width = r.Width, Height = r.Height,
                BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(thickness),
            };
            Canvas.SetLeft(b, r.X);
            Canvas.SetTop(b, r.Y);
            marks.Children.Add(b);
        }

        foreach (BoardRow row in layout.Rows)
        {
            Outline(row.Bounds, Color.Parse("#38BDF8"), 1); // row band — what a branch drag grabs
            // Every insertion point, drawn as the map's own drop caret is (a filled bar, not an outline).
            for (int i = 0; i <= row.DesktopCount; i++)
            {
                var caret = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 3, Height = row.TileHeight + 8, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(Color.Parse("#F472B6")),
                };
                Canvas.SetLeft(caret, row.BoundaryX(i) - 1.5);
                Canvas.SetTop(caret, row.TileTop - 4);
                marks.Children.Add(caret);
            }
        }
        // Every row boundary, drawn as the map's branch-drop separator is: across both rows it splits.
        for (int b = 0; b < layout.BoundaryCount; b++)
        {
            (double left, double right) = layout.BoundarySpan(b);
            var sep = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = right - left, Height = 3, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Color.Parse("#FBBF24")),
            };
            Canvas.SetLeft(sep, left);
            Canvas.SetTop(sep, layout.BoundaryY(b) - 1.5);
            marks.Children.Add(sep);
        }

        foreach (BoardTile tile in layout.Tiles)
            Outline(tile.Bounds, Color.Parse("#34D399"), 1); // tile hit rects

        Save(new Panel { Children = { board, marks } }, path);
    }

    // Render the metro-map view of a board to PNG, over the same dark ground the real overlay uses.
    private static void SaveMetro(NavMap map, string path)
        => Save(MetroView.Render(map, ScreenW, ScreenH, 1.0), path);

    private static void Save(NavMap map, string path)
        // Pass delete callbacks so the × badges render in the verification shot.
        => Save(BoardView.Render(map, ScreenW, ScreenH, 1.0, onTopDelete: _ => { }, onBranchDelete: (_, _) => { }), path);

    private static void Save(Control content, string path)
    {
        var host = new Border
        {
            Width = ScreenW, Height = ScreenH,
            Background = new SolidColorBrush(Color.Parse("#0F131B")), // design --bg (dark)
            Child = content,
        };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(new Size(ScreenW, ScreenH)));

        var rtb = new RenderTargetBitmap(new PixelSize(ScreenW, ScreenH), new Vector(96, 96));
        rtb.Render(host);
        using var fs = File.Create(path);
        rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({ScreenW}x{ScreenH})");
    }
}
