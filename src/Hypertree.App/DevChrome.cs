using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Hypertree.App;

/// <summary>
/// Dev-build "warpaint": on a Debug build (what <c>run.bat</c> produces; a real release is compiled
/// Release), the app dresses itself pink so a test copy is never mistaken for the installed one — every
/// icon it shows is tinted pink, and the full-screen overlay gains a pink screen border. All of it is
/// gated on <see cref="Active"/>, so a release build is byte-for-byte unaffected.
/// </summary>
internal static class DevChrome
{
    /// <summary>True on a Debug (dev) build only.</summary>
    public static bool Active =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>A hot pink that reads at a glance as "not the real build".</summary>
    public static readonly Color Pink = Color.FromRgb(0xFF, 0x2D, 0x9B);

    /// <summary>The pink used for the overlay's dev border.</summary>
    public static IBrush PinkBrush { get; } = new SolidColorBrush(Pink);

    /// <summary>How thick the overlay's dev border is (device-independent pixels).</summary>
    public const double BorderThickness = 5;

    private static readonly Uri PngUri = new("avares://hypertree/Assets/icon.png");
    private static readonly Uri IcoUri = new("avares://hypertree/Assets/icon.ico");

    /// <summary>The app logo as a bitmap — tinted pink on a dev build, the plain logo otherwise. Used by
    /// the switcher's header/bubble and as the source for <see cref="AppWindowIcon"/>.</summary>
    public static Bitmap AppLogo()
    {
        var src = new Bitmap(AssetLoader.Open(PngUri));
        return Active ? Tint(src, Pink) : src;
    }

    /// <summary>A window / tray icon: the tinted PNG on a dev build, the real multi-resolution <c>.ico</c>
    /// otherwise. Every window icon and the tray icon route through here.</summary>
    public static WindowIcon AppWindowIcon()
        => Active ? new WindowIcon(AppLogo()) : new WindowIcon(AssetLoader.Open(IcoUri));

    // Blend every non-transparent pixel toward `target`, keeping its alpha, so the logo's shape survives
    // but its colour reads pink. A decoded PNG gives Bgra8888 premultiplied pixels, so the target is scaled
    // by alpha to stay in premultiplied space. Dev-only cosmetic — exactness isn't the point, legibility is.
    private static Bitmap Tint(Bitmap src, Color target)
    {
        PixelSize size = src.PixelSize;
        int stride = size.Width * 4;
        int len = stride * size.Height;

        var bytes = new byte[len];
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            src.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), len, stride);

            const double f = 0.6; // tint strength
            for (int i = 0; i < len; i += 4)
            {
                double a = bytes[i + 3] / 255.0;
                if (a <= 0) continue;
                bytes[i + 0] = (byte)(bytes[i + 0] * (1 - f) + target.B * a * f); // B
                bytes[i + 1] = (byte)(bytes[i + 1] * (1 - f) + target.G * a * f); // G
                bytes[i + 2] = (byte)(bytes[i + 2] * (1 - f) + target.R * a * f); // R
            }

            var wb = new WriteableBitmap(size, src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (ILockedFramebuffer fb = wb.Lock())
            {
                if (fb.RowBytes == stride)
                    Marshal.Copy(bytes, 0, fb.Address, len);
                else // the framebuffer may be padded — copy a row at a time
                    for (int y = 0; y < size.Height; y++)
                        Marshal.Copy(bytes, y * stride, fb.Address + y * fb.RowBytes, stride);
            }
            return wb;
        }
        finally { handle.Free(); }
    }
}
