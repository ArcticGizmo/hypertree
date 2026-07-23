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

        List<NavMapTile> Top(int current) => new()
        {
            new("Home", current == 0), new("Comms", current == 1),
            new("Web", current == 2), new("Notes", current == 3),
        };
        NavMapGroup Feat(bool live, int cur) => new(0, "FEAT-123", new List<NavMapTile>
        {
            new("SPA", live && cur == 0), new("API", live && cur == 1), new("Mobile", live && cur == 2),
        }, live, cur);
        NavMapGroup Hotfix() => new(1, "hotfix", new List<NavMapTile> { new("db", false), new("api", false) }, false, 0);

        // On the main timeline: Web (cursor 2) current; both groups render below main (TopPosition 0).
        Save(new NavMap(Top(2), 2, true, new List<NavMapGroup> { Feat(false, 1), Hotfix() }, 0),
             Path.Combine(outDir, "board-top-row.png"));

        // Inside the current group (FEAT-123, on API=cursor 1), which sits directly below main
        // (TopPosition 0); hotfix rests below it on its cursor.
        Save(new NavMap(Top(-1), 2, false, new List<NavMapGroup> { Feat(true, 1), Hotfix() }, 0),
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
