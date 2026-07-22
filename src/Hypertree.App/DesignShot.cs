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

        var anchors = new List<NavMapAnchor>
        {
            new("Home", false, false), new("Comms", false, false),
            new("Web", true, true),    new("Notes", false, false),
        };
        var resting = new List<NavMapDesktop> { new("SPA", false), new("API", false), new("Mobile", false) };
        var dived   = new List<NavMapDesktop> { new("SPA", true),  new("API", false), new("Mobile", false) };

        Save(new NavMap(anchors, false, "FEAT-123", resting), Path.Combine(outDir, "board-top-row.png"));
        Save(new NavMap(anchors, true,  "FEAT-123", dived),   Path.Combine(outDir, "board-dived.png"));
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
