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
        NavMapGroup Feat(bool live, int cur) => new(0, "FEAT-123", new List<NavMapTile>
        {
            new("SPA", live && cur == 0, WindowCount: 3), new("API", live && cur == 1, WindowCount: 1),
            new("Mobile", live && cur == 2, WindowCount: 0),
        }, live, cur);
        NavMapGroup Hotfix() => new(1, "hotfix", new List<NavMapTile>
            { new("db", false, WindowCount: 1), new("api", false, WindowCount: 0) }, false, 0);

        // Stable pivot: FEAT-123 sits above main, hotfix below (main slot 1). On the main timeline,
        // Web (cursor 2) is current and main renders between the two groups.
        Save(new NavMap(Top(2), 2, true, new List<NavMapGroup> { Feat(false, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-top-row.png"));

        // Same fixed layout, now with the cursor inside FEAT-123 (on API=cursor 1) — the group above
        // main. Main keeps its slot; it does not move. We dived from Web, so it wears the green
        // "came from" outline.
        Save(new NavMap(Top(2, here: 2), 2, false, new List<NavMapGroup> { Feat(true, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-dived.png"));
    }

    // A representative primary-monitor size, so the shot shows the real full-screen, centred layout
    // (F1/F3) rather than a size-to-content card.
    private const int ScreenW = 1440, ScreenH = 900;

    private static void Save(NavMap map, string path)
    {
        var host = new Border
        {
            Width = ScreenW, Height = ScreenH,
            Background = new SolidColorBrush(Color.Parse("#0F131B")), // design --bg (dark)
            // Pass delete callbacks so the × badges render in the verification shot.
            Child = BoardView.Render(map, ScreenW, ScreenH, 1.0, onTopDelete: _ => { }, onGroupDelete: (_, _) => { }),
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
