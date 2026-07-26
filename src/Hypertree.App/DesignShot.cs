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
