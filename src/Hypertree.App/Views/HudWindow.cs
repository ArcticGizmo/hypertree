using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The transient navigation HUD: the flash. On each hotkey move it shows the full Model-P board
/// centred on the primary screen over a dimmed backdrop — the exact same board the interactive map
/// draws (F1: one presentation, two modes). It stays up while you hold the nav modifiers (Ctrl+Alt),
/// so you get time to find your bearings mid-navigation, and only fades out a short beat after you
/// release them. This is the transient mode: click-through and non-activating, so it never blocks
/// input or steals focus. The interactive mode (<see cref="MapOverlay"/>) draws the same board but
/// stays open, takes clicks, and pins across desktops.
/// </summary>
internal sealed class HudWindow : Window
{
    // Poll the modifier state (no focused window ⇒ no key-up events) and keep the flash up while
    // Ctrl+Alt is held; once released, hide after a short grace so the final position lingers.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int GraceTicks = 2; // ~100ms after release before hiding (snappy, with a little debounce)
    private readonly DispatcherTimer _poll;
    private int _grace;

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

        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
    }

    // While the nav modifiers are held, keep the flash up (and re-arm the grace). Once they're
    // released, count the grace down and hide.
    private void Poll()
    {
        if (ModifiersHeld()) { _grace = GraceTicks; return; }
        if (--_grace <= 0) { _poll.Stop(); IsVisible = false; }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Show the board centred on the primary screen; it stays up while Ctrl+Alt is held.</summary>
    public void Flash(NavMap map)
    {
        if (!IsVisible) Show();   // realizes the handle so Screens is available
        CoverPrimary();           // sets Width/Height to the primary screen (DIPs)
        Content = BoardView.Render(map, Width, Height); // board centres itself within the full screen
        Topmost = true;
        BringToTop();             // the desktop switch can briefly surface the target window over us

        _grace = GraceTicks;
        if (!_poll.IsEnabled) _poll.Start();
    }

    // Re-lift to the top of the always-on-top band. Non-activating, so the flash keeps its
    // no-focus-steal contract even while re-asserting z-order after a desktop switch.
    private void BringToTop()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return;
        SetWindowPos(handle.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(nint hWnd, int nIndex, long dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int VK_CONTROL = 0x11, VK_MENU = 0x12; // Ctrl, Alt — the nav modifier layer
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private static bool ModifiersHeld()
        => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    private void MakeClickThrough()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return;
        long ex = GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        SetWindowLongPtr(handle.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
