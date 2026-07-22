using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The HUD overlay — the source-of-truth "where am I" (PLAN.md §3.4), rendered as a small MAP of the
/// 2-D structure rather than a single line: the day-to-day anchor row, and the scope hanging off the
/// current anchor above it, with "you are here" highlighted. Native Task View can't show the depth
/// axis, so this is what makes Model P legible. Borderless, transparent, click-through, never steals
/// focus; centered horizontally and sitting just above the primary taskbar. Flash-on-navigation (M1).
/// </summary>
internal sealed class HudWindow : Window
{
    private readonly Border _chip;
    private readonly DispatcherTimer _hideTimer;

    public HudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsVisible = false;

        _chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x18, 0x18, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9),
        };
        Content = _chip;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); IsVisible = false; };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Render <paramref name="map"/> as the board, show the chip, and restart the auto-hide timer.</summary>
    public void Flash(NavMap map)
    {
        _chip.Child = BoardView.Render(map, 0.8, maxGroups: 1); // compact: nearest group only

        if (!IsVisible) Show(); // first show creates the handle (OnOpened → click-through)
        Reposition();
        Topmost = true;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // ── Placement: centered horizontally, just below the top of the primary screen ─

    private void Reposition()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double scale = screen.Scaling;
        int w = (int)Math.Ceiling(Bounds.Width * scale);
        int h = (int)Math.Ceiling(Bounds.Height * scale);

        PixelRect full = screen.Bounds;
        PixelRect work = screen.WorkingArea;   // excludes the taskbar

        // Top-center: the bottom-center is taken by Windows' own "Desktop N" switch indicator, which
        // the flash would fight with. Sit just below the top edge of the work area instead.
        int x = full.X + (full.Width - w) / 2;
        int y = work.Y + 12;
        Position = new PixelPoint(x, y);
    }

    // ── Click-through + no focus steal ─────────────────────────────────────────────
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
