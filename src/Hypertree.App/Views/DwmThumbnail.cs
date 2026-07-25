using System.Runtime.InteropServices;

namespace Hypertree.App.Views;

/// <summary>
/// A single live DWM thumbnail: the OS composites a scaled, live image of a source window into a
/// rectangle of a destination window (our move overlay). Used to give each window card in the
/// "move windows" picker a Task-View-style live preview. RAII — <see cref="Dispose"/> unregisters it.
///
/// The thumbnail is painted by DWM <em>on top of</em> everything the destination window draws in that
/// rectangle, so the caller must reserve the rect (draw card chrome — border, caption, checkbox —
/// around it, never under it). <see cref="Place"/> takes the rect in the destination window's
/// <b>physical client pixels</b> (Avalonia DIPs × the window's RenderScaling).
/// </summary>
internal sealed class DwmThumbnail : IDisposable
{
    private nint _thumb;

    /// <summary>Whether the thumbnail registered successfully (some windows can't be thumbnailed —
    /// the caller falls back to a plain card body).</summary>
    public bool Ok => _thumb != 0;

    public DwmThumbnail(nint destination, nint source)
    {
        if (destination == 0 || source == 0 || DwmRegisterThumbnail(destination, source, out _thumb) != 0)
            _thumb = 0;
    }

    /// <summary>Position (and show) the thumbnail at a rect in the destination window's physical
    /// client pixels. Preserves aspect ratio; the whole source window (frame included) is shown so
    /// it reads like Task View.</summary>
    public void Place(int left, int top, int right, int bottom)
    {
        if (_thumb == 0) return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TN_RECTDESTINATION | DWM_TN_VISIBLE | DWM_TN_OPACITY,
            rcDestination = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    /// <summary>Hide the thumbnail without unregistering it — for cards scrolled out of the viewport,
    /// so DWM doesn't paint them over the header/scroll edges (it can't clip to a region).</summary>
    public void Hide()
    {
        if (_thumb == 0) return;
        var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TN_VISIBLE, fVisible = false };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Dispose()
    {
        if (_thumb != 0) { DwmUnregisterThumbnail(_thumb); _thumb = 0; }
    }

    private const int DWM_TN_RECTDESTINATION = 0x1, DWM_TN_OPACITY = 0x4, DWM_TN_VISIBLE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}
