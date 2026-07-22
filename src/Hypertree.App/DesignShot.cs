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

        // On the top row: Web (cursor 2) current, groups resting beneath, each centred on its cursor.
        Save(new NavMap(Top(2), 2, true, new List<NavMapGroup> { Feat(false, 1), Hotfix() }),
             Path.Combine(outDir, "board-top-row.png"));

        // Dived into the active group (FEAT-123, on API=cursor 1); hotfix rests below on its cursor.
        Save(new NavMap(Top(-1), 2, false, new List<NavMapGroup> { Feat(true, 1), Hotfix() }),
             Path.Combine(outDir, "board-dived.png"));
    }

    private static void Save(NavMap map, string path)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0F131B")), // design --bg (dark)
            Padding = new Thickness(24),
            Child = BoardView.Render(map, 1.0),
        };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(host.DesiredSize));

        int w = (int)Math.Ceiling(host.DesiredSize.Width);
        int h = (int)Math.Ceiling(host.DesiredSize.Height);
        var rtb = new RenderTargetBitmap(new PixelSize(Math.Max(1, w), Math.Max(1, h)), new Vector(96, 96));
        rtb.Render(host);
        using var fs = File.Create(path);
        rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({w}x{h})");
    }
}
