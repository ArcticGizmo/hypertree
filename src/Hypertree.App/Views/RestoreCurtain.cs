using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.WindowLayout;

namespace Hypertree.App.Views;

/// <summary>
/// A black "loading" curtain drawn over every monitor while a restore runs, so the visible popping of the
/// maximize-cross-monitor trick (restore→maximize) happens <em>behind</em> a fade rather than in the user's
/// face. Fades in, runs the restore once fully opaque, then fades out — even if the restore throws. Only
/// worth showing when that trick is actually needed (<see cref="RestorePlan.NeedsCurtain"/>); a plain move
/// doesn't pop, so the app skips the curtain there.
/// </summary>
/// <remarks>
/// One window spanning the whole virtual desktop, sized from the layout controller's own monitor bounds
/// (physical pixels) so it doesn't depend on the Avalonia Screens API. It never takes focus
/// (<c>WS_EX_NOACTIVATE</c> + tool window, <c>ShowActivated=false</c>) — the restore preserves the real
/// foreground itself, and the curtain must not fight it.
/// </remarks>
internal sealed class RestoreCurtain : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;

    private readonly Border _fill;

    private RestoreCurtain(PixelRect boundsPx, double dips, string message)
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Position = boundsPx.Position;                     // physical-pixel top-left of the virtual desktop
        Width = boundsPx.Width / dips;                    // convert to DIPs; overshoot is clipped (black off-screen)
        Height = boundsPx.Height / dips;

        _fill = new Border
        {
            Background = Brushes.Black,
            Opacity = 0,
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.Parse("#E8EDF5")),
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Content = _fill;
    }

    /// <summary>
    /// Run <paramref name="apply"/> behind a fade over every monitor. <paramref name="monitors"/> supplies
    /// the virtual-desktop bounds and scale. Must be called on the UI thread; returns immediately (the
    /// sequence is timer-driven) — <paramref name="apply"/> runs when the curtain is fully opaque.
    /// </summary>
    public static void Run(IReadOnlyList<MonitorRef> monitors, string message, Action apply)
    {
        if (monitors.Count == 0) { apply(); return; }

        int left = monitors.Min(m => m.Bounds.Left), top = monitors.Min(m => m.Bounds.Top);
        int right = monitors.Max(m => m.Bounds.Left + m.Bounds.Width), bottom = monitors.Max(m => m.Bounds.Top + m.Bounds.Height);
        // Convert physical → DIPs using the smallest scale present, so the (single) window is big enough to
        // cover every monitor even under mixed DPI; the overshoot is off-desktop and invisible.
        double minScale = monitors.Min(m => m.Dpi <= 0 ? 1.0 : m.Dpi / 96.0);
        var boundsPx = new PixelRect(left, top, right - left, bottom - top);

        var curtain = new RestoreCurtain(boundsPx, minScale, message);
        curtain.Show();
        if (curtain.TryGetPlatformHandle() is { } h) curtain.MakeNoActivate(h.Handle);
        curtain.WriteRun(apply);
    }

    private void WriteRun(Action apply)
    {
        // Fade in → (fully black) run the restore → fade out → close. Try/finally so a throwing restore still
        // lifts the curtain rather than leaving a black screen up.
        Fade(0, 1, () =>
        {
            try { apply(); }
            finally { Fade(1, 0, Close); }
        });
    }

    // Step Border opacity over ~180ms on a UI-thread timer (predictable, no animation-system dependency).
    private void Fade(double from, double to, Action done)
    {
        const int steps = 12;
        const int ms = 15;
        int i = 0;
        _fill.Opacity = from;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            i++;
            _fill.Opacity = from + (to - from) * i / (double)steps;
            if (i >= steps)
            {
                timer.Stop();
                _fill.Opacity = to;
                done();
            }
        };
        timer.Start();
    }

    private void MakeNoActivate(nint hwnd)
    {
        if (hwnd == 0) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(nint hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(nint hWnd, int nIndex, long dwNewLong);
}
