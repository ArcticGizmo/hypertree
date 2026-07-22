using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Hypertree.App;

/// <summary>
/// Renders the tray icon at runtime (a green rounded square with a downward "dive" triangle) so M1
/// ships no binary asset yet. A real icon pipeline (tools/IconGen from an SVG, like perch) is an
/// M3 polish item.
/// </summary>
internal static class TrayIconFactory
{
    public static WindowIcon Create()
    {
        var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#2D7D46")), null,
                new RoundedRect(new Rect(1, 1, 30, 30), 7));
            var triangle = new PolylineGeometry(
                new[] { new Point(9, 11), new Point(23, 11), new Point(16, 22) }, isFilled: true);
            ctx.DrawGeometry(Brushes.White, null, triangle);
        }

        var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
