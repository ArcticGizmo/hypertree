using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The transient navigation HUD. On each hotkey move it shows the full Model-P board pinned to the
/// top of the primary screen over a dimmed backdrop (the same board the interactive overlay draws —
/// not a small chip), then auto-hides. Covers the whole primary screen and is laid out with plain
/// alignment (no manual pixel maths), so the board is reliably top-centred — which also fixes the
/// occasional "stuck at top-left" glitch the old size-to-content chip had. Click-through and
/// non-activating: it never blocks input or steals focus.
/// </summary>
internal sealed class HudWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public HudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x10, 0x10)); // dim backdrop
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsVisible = false;
        Position = new PixelPoint(0, 0);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); IsVisible = false; };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Show the board at the top of the primary screen and restart the auto-hide timer.</summary>
    public void Flash(NavMap map)
    {
        Control board = BoardView.Render(map, 1.0);
        board.HorizontalAlignment = HorizontalAlignment.Center; // centred by layout — no Bounds maths
        board.VerticalAlignment = VerticalAlignment.Top;
        board.Margin = new Thickness(0, 40, 0, 0);
        Content = board;

        if (!IsVisible) Show();   // realizes the handle so Screens is available
        CoverPrimary();
        Topmost = true;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void CoverPrimary()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
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
