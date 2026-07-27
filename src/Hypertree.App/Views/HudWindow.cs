using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Platform;
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
    // Poll the modifier state (no focused window ⇒ no key-up events). The flash stays up while the
    // navigation modifiers are held and hides a short grace after release; if a navigation was bound to
    // a chord with no modifiers to hold, it falls back to a fixed on-screen timeout. Timings are fixed
    // constants (no longer user-configurable).
    private const int PollMs = 50;
    private const int GraceTicks = 100 / PollMs;   // ~100ms after release before hiding
    private const int TimeoutTicks = 1500 / PollMs; // ~1500ms fixed on-screen time (no-modifier fallback)
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(PollMs);
    private readonly DispatcherTimer _poll;
    private HotkeyModifiers _holdMods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private int _remaining;

    // The board's slide-in tween (the optional directional animation). At most one runs at a time: each
    // flash rebuilds the board control, so a press landing mid-slide stops the previous tween before
    // animating the new board. A hand-rolled DispatcherTimer tween (matching the app's timer style) keeps
    // the effect off the render thread's transition machinery, which the overlays force-disable elsewhere.
    private DispatcherTimer? _slide;
    private const int SlideMs = 170;     // total slide-in duration
    private const int SlideTickMs = 15;  // ~66fps tween step

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

    // Re-arm the grace while the navigation modifiers are held, then count down after release. When the
    // flash was raised with no modifiers (e.g. a result flash, or a nav bound to a bare key), there's
    // nothing to hold, so it just counts the fixed timeout down.
    private void Poll()
    {
        if (ModifierKeys.ModifiersHeld(_holdMods)) { _remaining = GraceTicks; return; }
        if (--_remaining <= 0) { _poll.Stop(); IsVisible = false; }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Show the board centred on the primary screen; it stays up while <paramref name="holdMods"/>
    /// are held (pass <see cref="HotkeyModifiers.None"/> for a non-gesture flash that just times out).
    /// When <paramref name="animate"/> is set the board slides in from the direction of <paramref name="move"/>
    /// (a directional echo of the traditional desktop-switch slide); a null <paramref name="move"/> just fades.
    /// The caller gates <paramref name="animate"/> on the user setting and the OS "show animations" preference.</summary>
    public void Flash(NavMap map, HotkeyModifiers holdMods, NavAction? move = null, bool animate = false)
    {
        _holdMods = holdMods;
        if (!IsVisible) Show();   // realizes the handle so Screens is available
        CoverPrimary();           // sets Width/Height to the primary screen (DIPs)
        Control board = BoardView.Render(map, Width, Height); // board centres itself within the full screen
        Content = board;
        // A press landing mid-slide replaces the board, so retire the previous tween before it writes stale
        // transform values onto the now-orphaned control.
        _slide?.Stop();
        if (animate) AnimateIn(board, move);
        Topmost = true;
        BringToTop();             // the desktop switch can briefly surface the target window over us

        _remaining = holdMods != HotkeyModifiers.None ? GraceTicks : TimeoutTicks;
        if (!_poll.IsEnabled) _poll.Start();
    }

    // Slide the freshly-rendered board in from the direction of travel while fading it up. Content-enters-
    // from-the-direction-you-moved: press right and the board arrives from the right, matching the OS slide.
    // A null move (a bare "show before moving" reveal, or a result flash) just fades — there's no direction.
    private void AnimateIn(Control board, NavAction? move)
    {
        (double startX, double startY) = SlideOffset(move);
        var slide = new TranslateTransform(startX, startY);
        board.RenderTransform = slide;
        board.Opacity = 0;

        int total = Math.Max(1, SlideMs / SlideTickMs);
        int tick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlideTickMs) };
        timer.Tick += (_, _) =>
        {
            double e = EaseOutCubic(Math.Min(1.0, (double)++tick / total));
            slide.X = startX * (1 - e);
            slide.Y = startY * (1 - e);
            board.Opacity = e;
            if (tick < total) return;
            timer.Stop();
            slide.X = 0; slide.Y = 0; board.Opacity = 1;
            if (ReferenceEquals(_slide, timer)) _slide = null;
        };
        _slide = timer;
        timer.Start();
    }

    // The board's starting offset for a slide, as a fraction of the screen so it scales with resolution.
    // A subtle nudge, not a full-screen sweep: this overlay is a cue over the (already-switched) desktop,
    // so a small directional slide reads without feeling like a second, competing transition.
    private (double X, double Y) SlideOffset(NavAction? move)
    {
        double dx = Width * 0.05, dy = Height * 0.05;
        return move switch
        {
            NavAction.MoveRight => (dx, 0),
            NavAction.MoveLeft => (-dx, 0),
            NavAction.Dive => (0, dy),
            NavAction.Surface => (0, -dy),
            _ => (0, 0), // no direction — fade only
        };
    }

    private static double EaseOutCubic(double t) { double u = 1 - t; return 1 - u * u * u; }

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


    private void MakeClickThrough()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return;
        long ex = GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        SetWindowLongPtr(handle.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
