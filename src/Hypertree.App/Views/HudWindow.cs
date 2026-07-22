using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Hypertree.App.Views;

/// <summary>
/// The HUD chip — the source-of-truth "where am I" readout (PLAN.md §3.4). A small, borderless,
/// transparent, always-on-top, click-through window centered horizontally over the primary monitor's
/// taskbar (placement decided with the user). It never takes focus and never appears in the taskbar
/// or Alt-Tab. M1 is flash-on-switch: <see cref="Flash"/> shows it briefly after each navigation.
/// </summary>
internal sealed class HudWindow : Window
{
    private readonly TextBlock _label;
    private readonly DispatcherTimer _hideTimer;

    public HudWindow()
    {
        // Chrome-less, transparent, non-activating overlay.
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsVisible = false;

        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 6),
            Child = _label,
        };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); IsVisible = false; };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Show the chip with <paramref name="text"/> and restart the auto-hide timer.</summary>
    public void Flash(string text)
    {
        _label.Text = text;

        if (!IsVisible)
        {
            Show();          // first show creates the handle (OnOpened → click-through)
        }
        Reposition();
        Topmost = true;       // stay above a just-switched foreground window

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Center horizontally on the primary screen, vertically within the bottom taskbar band.</summary>
    private void Reposition()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double scale = screen.Scaling;
        int w = (int)Math.Ceiling(Bounds.Width * scale);
        int h = (int)Math.Ceiling(Bounds.Height * scale);

        PixelRect full = screen.Bounds;
        PixelRect work = screen.WorkingArea;
        int taskbarHeight = full.Bottom - work.Bottom; // >0 for a bottom taskbar

        int x = full.X + (full.Width - w) / 2;
        int y = taskbarHeight > 0
            ? work.Bottom + (taskbarHeight - h) / 2   // vertically centered in the taskbar strip
            : full.Bottom - h - 8;                     // fallback: hug the bottom
        Position = new PixelPoint(x, y);
    }

    // ── Click-through: WS_EX_TRANSPARENT so clicks pass to the taskbar beneath; NOACTIVATE so the
    //    chip never steals focus from the app you just switched to. ─────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(nint hWnd, int nIndex, long dwNewLong);

    private void MakeClickThrough()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return;
        long ex = GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        SetWindowLongPtr(handle.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
