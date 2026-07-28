using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;

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

    // The transition tween (the optional directional animation). At most one runs at a time: each flash
    // rebuilds the board control, so a press landing mid-wipe stops the previous tween before animating the
    // new board. A hand-rolled DispatcherTimer tween (matching the app's timer style) keeps the effect off
    // the render thread's transition machinery, which the overlays force-disable elsewhere.
    private DispatcherTimer? _slide;
    private const int SlideMs = 260;     // total wipe duration
    private const int SlideTickMs = 15;  // ~66fps tween step

    // The dim backdrop, held as a field so the transition can fade it in (0→full) rather than snap it on —
    // the snap was the most visible part of the motion. Shares the interactive map's vignette (StageWindow's
    // DimBg) so the transient flash and the full map read at the same weight — the board keeps its contrast
    // over a busy desktop either way. Its own instance, since the fade-in mutates Opacity.
    private readonly RadialGradientBrush _dim = StageWindow.BuildDim();

    // Peak darkness of the sweeping wipe band, over #101010. Tunable: this is the "how strong is the wipe".
    private const byte BandAlpha = 0x6E;

    public HudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = _dim; // dim backdrop
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
    /// When <paramref name="animate"/> is set, a directional move plays a soft gradient wipe for
    /// <paramref name="move"/>; <paramref name="fromLeadingEdge"/> picks which edge it starts on (the edge you
    /// moved toward, or the opposite one). The board itself is shown at once — only the background (dim + wipe)
    /// animates. The caller gates <paramref name="animate"/> on the user setting and the OS "show animations"
    /// preference.</summary>
    public void Flash(NavMap map, HotkeyModifiers holdMods, NavAction? move = null, bool animate = false,
                      bool fromLeadingEdge = true, MapStyle style = MapStyle.Board)
    {
        _holdMods = holdMods;
        if (!IsVisible) Show();   // realizes the handle so Screens is available
        CoverPrimary();           // sets Width/Height to the primary screen (DIPs)

        // The flash is transient, so the metro train doesn't pulse here (animate:false) — the wipe below is
        // the only motion. board centres itself within the full screen.
        Control board = MapSurface.Render(map, Width, Height, style);

        // A directional move gets a soft gradient wipe: a dark band that begins on the edge opposite the
        // arrow and sweeps across toward the way you pressed, uncovering the (already-switched) desktop as
        // it passes. A reveal press / result flash has no direction, so there's no wipe — just the board.
        Border? veil = animate && move is not null ? BuildSweepVeil(move.Value) : null;

        var root = new Panel();
        if (veil is not null) root.Children.Add(veil);
        root.Children.Add(board);
        Content = root;

        // A press landing mid-wipe replaces the content, so retire the previous tween before it writes
        // stale values onto the now-orphaned controls.
        _slide?.Stop();
        if (animate) AnimateIn(board, veil, move, fromLeadingEdge);
        else _dim.Opacity = 1; // snap: full dim, matching the pre-animation look
        Topmost = true;
        BringToTop();             // the desktop switch can briefly surface the target window over us

        _remaining = holdMods != HotkeyModifiers.None ? GraceTicks : TimeoutTicks;
        if (!_poll.IsEnabled) _poll.Start();
    }

    // Drive the transition off one tween: the board is shown at once (no fade), the dim fades in behind it,
    // and the wipe band — when the move has a direction — sweeps from the edge opposite the arrow off the
    // pressed edge, so the desktop is uncovered in the direction you moved. The background carries the motion;
    // the map/board does not. A null move (reveal / result flash) just fades the dim up.
    private void AnimateIn(Control board, Border? veil, NavAction? move, bool fromLeadingEdge)
    {
        board.Opacity = 1; // the board itself no longer animates — it's shown at once; only the background moves
        _dim.Opacity = 0;

        TranslateTransform? veilT = null;
        bool horizontal = true;
        double start = 0, end = 0;
        if (veil is not null && move is not null)
        {
            (horizontal, start, end) = SweepTravel(move.Value, fromLeadingEdge);
            veilT = new TranslateTransform(horizontal ? start : 0, horizontal ? 0 : start);
            veil.RenderTransform = veilT;
            veil.Opacity = 1;
        }

        int total = Math.Max(1, SlideMs / SlideTickMs);
        int tick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlideTickMs) };
        timer.Tick += (_, _) =>
        {
            double e = EaseInOutCubic(Math.Min(1.0, (double)++tick / total));
            _dim.Opacity = e;
            if (veilT is not null)
            {
                double p = start + (end - start) * e;
                veilT.X = horizontal ? p : 0;
                veilT.Y = horizontal ? 0 : p;
                veil!.Opacity = 1 - e; // dissolve the band as it travels, so it thins out toward the pressed edge
            }
            if (tick < total) return;
            timer.Stop();
            _dim.Opacity = 1; // the veil has swept off-screen; next flash rebuilds content
            if (ReferenceEquals(_slide, timer)) _slide = null;
        };
        _slide = timer;
        timer.Start();
    }

    // A full-screen band whose darkness follows a raised-cosine (Hann) curve along the move axis: zero at
    // both edges, peaking in the centre, with no apex kink — so the stripe eases in and out rather than
    // ramping to a point. Sampled at enough stops that the falloff reads as a smooth gradient, not facets.
    private Border BuildSweepVeil(NavAction move)
    {
        bool horizontal = move is NavAction.MoveLeft or NavAction.MoveRight;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, horizontal ? 0.5 : 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(horizontal ? 1 : 0, horizontal ? 0.5 : 1, RelativeUnit.Relative),
        };
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            double x = (double)i / steps;
            double bell = 0.5 * (1 - Math.Cos(2 * Math.PI * x)); // 0 at the edges, 1 at the centre
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(BandAlpha * bell), 0x10, 0x10, 0x10), x));
        }
        return new Border
        {
            Width = Width,
            Height = Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = brush,
            IsHitTestVisible = false,
        };
    }

    // Where the wipe band's centre travels, as a translate along the move axis. It starts on the edge
    // opposite the arrow and runs off the pressed edge (the band is one screen wide, so its centre offset
    // is the edge position minus half a screen). Press right ⇒ the band leaves the left edge and exits right.
    private (bool Horizontal, double Start, double End) SweepTravel(NavAction move, bool fromLeadingEdge)
    {
        double w = Width, h = Height;
        // Base mapping: the band starts on the edge OPPOSITE the arrow and sweeps toward the way you pressed.
        (bool horizontal, double start, double end) = move switch
        {
            NavAction.MoveRight => (true, -0.5 * w, w),   // centre: left edge → off the right
            NavAction.MoveLeft => (true, 0.5 * w, -w),    // right edge → off the left
            NavAction.Dive => (false, -0.5 * h, h),       // top edge → off the bottom
            NavAction.Surface => (false, 0.5 * h, -h),    // bottom edge → off the top
            _ => (true, 0.0, 0.0),
        };
        // Leading-edge start is the mirror image: begin on the edge you moved TOWARD and sweep away across.
        if (fromLeadingEdge) { start = -start; end = -end; }
        return (horizontal, start, end);
    }

    // Ease in and out: slow at both ends, quickest in the middle, so the wipe doesn't lurch off the mark.
    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

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
