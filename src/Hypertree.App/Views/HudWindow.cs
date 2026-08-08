using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.App.Views.Scene;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The transient navigation HUD: the flash. On each hotkey move it shows the full Model-P board
/// centred on the primary screen over a dimmed backdrop — the exact same board the interactive map
/// draws (F1: one presentation, two modes). It stays up while you hold the nav modifiers (Ctrl+Alt),
/// so you get time to find your bearings mid-navigation, and only fades out a short beat after you
/// release them. This is the transient mode: click-through and non-activating, so it never blocks
/// input or steals focus. The interactive mode (<see cref="SpatialOverlay"/>) draws the same board but
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
    // Whether a board is currently on screen. Drives the board's fade: it rises from nothing the first time
    // a board goes up, and never re-fades while one is already showing. Not the same as IsVisible — Cover()
    // shows the window with only the dim in it, before there's any board to show.
    private bool _hasBoard;
    // Whether the dim is deliberately already up (Cover() raised it ahead of a desktop switch), so the fade
    // must leave it alone. Tracked as intent rather than read back off _dim.Opacity: a tween can still be
    // mid-write when the next press lands, and inferring "already covered" from a live animated value read
    // a half-finished fade as a finished one — which silently disabled the fade on every later appearance.
    private bool _covered;
    // The fade-out tween is running (held in _slide like the others; this says which kind it is).
    private bool _hiding;

    // The transition tween (the optional directional animation). At most one runs at a time: each flash
    // rebuilds the board control, so a press landing mid-wipe stops the previous tween before animating the
    // new board. A hand-rolled DispatcherTimer tween (matching the app's timer style) keeps the effect off
    // the render thread's transition machinery, which the overlays force-disable elsewhere.
    private DispatcherTimer? _slide;
    private const int SlideMs = 260;     // total wipe duration
    private const int SlideTickMs = 15;  // ~66fps tween step
    // How long the flash takes to fade up when it appears from nothing (dim and board together). Shorter
    // than the wipe: the point is to take the hard edge off the onset, not to keep you waiting to read the
    // board. Independent of the wipe — see the two gates on Flash.
    private const int FadeMs = 170;
    // And how long it takes to fade back out. Longer than the way in, because this is the transition that
    // hurts: the board is a dark sheet over the desktop, so dropping it in one frame is a punch of light
    // straight back to full brightness — the more so the lighter the wallpaper behind it.
    private const int FadeOutMs = 220;

    // The dim backdrop, held as a field so the transition can fade it in (0→full) rather than snap it on —
    // the snap was the most visible part of the motion. Shares the interactive map's vignette (StageWindow's
    // DimBg) so the transient flash and the full map read at the same weight — the board keeps its contrast
    // over a busy desktop either way. Its own instance, since the fade-in mutates Opacity.
    private readonly RadialGradientBrush _dim = StageWindow.BuildDim();

    // Peak darkness of the sweeping wipe band, over #101010. Tunable: this is the "how strong is the wipe".
    private const byte BandAlpha = 0x6E;

    // The shared map camera (owned by App, also driving the interactive map). Flashing navigates it, so
    // opening the map lands on the same framing, and the flash pans by the same dead-zone rules.
    private readonly MapCamera _camera;

    public HudWindow(MapCamera camera)
    {
        _camera = camera;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = _dim; // dim backdrop
        _dim.Opacity = 0;  // resting state: nothing showing (a brush is born opaque, which would snap)
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
        if (--_remaining > 0) return;
        _poll.Stop();
        FadeOutAndHide();
    }

    // Ease the whole flash away rather than cutting it. Dropping a dark full-screen sheet in one frame
    // returns the desktop to full brightness instantly, which reads as a flash every bit as much as a hard
    // arrival does — and it fires on every disappearance, whether or not a desktop switch was involved.
    private void FadeOutAndHide()
    {
        // Whatever else was animating (a fade-in that hadn't finished, a wipe still travelling) must stop
        // here: a tween that outlives the hide goes on writing opacities afterwards, and the values it
        // leaves behind are what the next appearance starts from.
        _slide?.Stop();

        var content = Content as Control;
        double dimFrom = _dim.Opacity;
        double contentFrom = content?.Opacity ?? 1;
        int total = Math.Max(1, FadeOutMs / SlideTickMs);
        int tick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlideTickMs) };
        timer.Tick += (_, _) =>
        {
            double e = EaseOutCubic(Math.Min(1.0, (double)++tick / total));
            _dim.Opacity = dimFrom * (1 - e);
            if (content is not null) content.Opacity = contentFrom * (1 - e);
            if (tick < total) return;
            timer.Stop();
            Settle();
        };
        _slide = timer;
        _hiding = true;
        timer.Start();
    }

    // Off screen and back to the resting state, so the next appearance starts from nothing.
    private void Settle()
    {
        IsVisible = false;
        _hasBoard = false;
        _covered = false;
        _hiding = false;
        _dim.Opacity = 0;
        if (Content is Control c) c.Opacity = 1;
        _slide = null;
    }

    /// <summary>
    /// Put the dim up <b>now</b>, at full strength, without touching the board.
    /// </summary>
    /// <remarks>
    /// Called immediately before a desktop switch. Without it, the switch completes — and Windows presents
    /// the destination desktop, foreground handover and all — while nothing of ours is covering the screen;
    /// the flash only went up afterwards. Measured at ~68ms of fully-lit destination desktop per move, which
    /// is the punch of light people read as "the overlay flashed". Covering first means the switch happens
    /// behind the dim instead, and all that changes on screen is the (already dimmed) content behind it.
    ///
    /// The dim snaps rather than fades here on purpose: a fade that hasn't finished isn't covering anything,
    /// and waiting for one would put ~150ms of latency on every navigation keystroke. The board still fades
    /// in behind it — see <see cref="Flash"/> — so the thing you actually read arrives softly.
    /// </remarks>
    public void Cover()
    {
        // A fade-out caught mid-flight would keep dimming us back down behind the switch.
        if (_hiding) { _slide?.Stop(); _hiding = false; if (Content is Control c) c.Opacity = 1; }
        if (!IsVisible) Show();  // realizes the handle so Screens is available
        CoverPrimary();
        _dim.Opacity = 1;
        _covered = true;
        Topmost = true;
        BringToTop();
        // Keep it alive across the switch; Flash re-arms this properly a moment later.
        if (_remaining <= 0) _remaining = GraceTicks;
        if (!_poll.IsEnabled) _poll.Start();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Show the board centred on the primary screen; it stays up while <paramref name="holdMods"/>
    /// are held (pass <see cref="HotkeyModifiers.None"/> for a non-gesture flash that just times out).</summary>
    /// <remarks>
    /// Two independent pieces of motion, because they answer different complaints:
    /// <list type="bullet">
    /// <item><paramref name="fade"/> — the <b>onset</b>. A board going up where there wasn't one fades in
    /// rather than snapping to full strength. A press landing on a board that's already showing never
    /// re-fades (that would pulse the screen once per keystroke of a held run). The dim comes up with it,
    /// starting from wherever it already is — so a <see cref="Cover"/> that has already raised it for a
    /// desktop switch is left alone, and a flash with no switch to cover still fades up from nothing.</item>
    /// <item><paramref name="animate"/> — the <b>travel</b>. A directional <paramref name="move"/> plays a soft
    /// gradient wipe, starting on the edge <paramref name="fromLeadingEdge"/> selects. This is what the
    /// "Animate navigation moves" setting governs.</item>
    /// </list>
    /// The board only ever fades, never moves. Callers gate both on the OS "show animations" preference, so
    /// reduce-motion still snaps.
    /// </remarks>
    public void Flash(NavMap map, HotkeyModifiers holdMods, NavAction? move = null, bool animate = false,
                      bool fromLeadingEdge = true, MapStyle style = MapStyle.Board, bool fade = false)
    {
        PrepareSurface();
        FlashBoard(MapSurface.Render(map, Width, Height, style, camera: _camera),
                   holdMods, move, animate, fromLeadingEdge, fade);
    }

    /// <summary>Flash the <b>spatial</b> board — the same transient HUD, drawn from the spatial scene so a
    /// navigation in spatial mode shows the layout you configured rather than the row list.</summary>
    public void Flash(SpatialScene scene, MapStyle style, HotkeyModifiers holdMods, NavAction? move = null,
                      bool animate = false, bool fromLeadingEdge = true, bool fade = false)
    {
        PrepareSurface();
        FlashBoard(SpatialPainter.Render(scene, Width, Height, 1.0, _camera, style: style),
                   holdMods, move, animate, fromLeadingEdge, fade);
    }

    // Realize + size the window so Width/Height are the primary screen before a board is built against them.
    private void PrepareSurface()
    {
        if (!IsVisible) Show();   // realizes the handle so Screens is available (Cover may have done this)
        CoverPrimary();           // sets Width/Height to the primary screen (DIPs)
    }

    // Put a freshly-built board on screen with the shared fade/wipe behaviour — the body both Flash overloads
    // share, differing only in how the board control was drawn.
    private void FlashBoard(Control board, HotkeyModifiers holdMods, NavAction? move, bool animate,
                            bool fromLeadingEdge, bool fade)
    {
        _holdMods = holdMods;
        // Whether this press puts a board up where there wasn't one, as opposed to updating one that's
        // already showing. Read before _hasBoard is set below.
        bool cold = !_hasBoard;

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
        // Fade only when a board is arriving; wipe only when there's a direction to carry. Either one needs
        // the tween — with neither, the flash snaps in as it always did.
        bool fadeIn = fade && cold;
        _slide?.Stop();  // retires a fade-out as well as a previous fade-in / wipe
        _hiding = false;
        if (fadeIn || veil is not null) AnimateIn(board, veil, move, fromLeadingEdge, fadeIn);
        else { _dim.Opacity = 1; board.Opacity = 1; } // snap: full dim, matching the pre-animation look
        _hasBoard = true;
        Topmost = true;
        BringToTop();             // the desktop switch can briefly surface the target window over us

        _remaining = holdMods != HotkeyModifiers.None ? GraceTicks : TimeoutTicks;
        if (!_poll.IsEnabled) _poll.Start();
    }

    // One tween drives both effects, so they can't drift apart on screen.
    //
    // <paramref name="fade"/> — the onset: dim and board rise together from nothing on one ease-out curve, so
    // the flash arrives as a single soft swell. They share a curve deliberately: fading the board while the dim
    // was still easing up (or worse, snapping the board in over an undimmed desktop) is what read as a hard
    // punch of light. Off for a press landing on a flash that's already up — re-fading then would drop the
    // backdrop to clear and darken it again once per keystroke of a held run, pulsing the whole screen.
    //
    // The wipe band, when the move has a direction, sweeps from one edge off the other on its own ease-in-out
    // curve, uncovering the (already-switched) desktop in the direction you moved. The background carries all
    // the travel; the board only ever fades, never moves.
    private void AnimateIn(Control board, Border? veil, NavAction? move, bool fromLeadingEdge, bool fade)
    {
        board.Opacity = fade ? 0 : 1;
        // Where the dim starts. Cover() has already put it up for a desktop switch (_covered), so it holds
        // still and only the board fades; with no switch behind it the dim swells up from nothing alongside
        // the board. Keyed off intent, never off _dim.Opacity — see the _covered field.
        double dimFrom = fade && !_covered ? 0 : 1;
        _dim.Opacity = dimFrom;

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

        int fadeTotal = Math.Max(1, FadeMs / SlideTickMs);
        int wipeTotal = Math.Max(1, SlideMs / SlideTickMs);
        // Run for as long as the longer effect needs. The fade is always the shorter of the two, so a wipe
        // decides the length whenever there is one.
        int total = veilT is not null ? wipeTotal : fadeTotal;
        int tick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlideTickMs) };
        timer.Tick += (_, _) =>
        {
            ++tick;
            if (fade)
            {
                double rise = EaseOutCubic(Math.Min(1.0, (double)tick / fadeTotal));
                _dim.Opacity = dimFrom + (1 - dimFrom) * rise; // a covered dim (dimFrom == 1) holds still
                board.Opacity = rise;
            }
            if (veilT is not null)
            {
                double e = EaseInOutCubic(Math.Min(1.0, (double)tick / wipeTotal));
                double p = start + (end - start) * e;
                veilT.X = horizontal ? p : 0;
                veilT.Y = horizontal ? 0 : p;
                veil!.Opacity = 1 - e; // dissolve the band as it travels, so it thins out toward the pressed edge
            }
            if (tick < total) return;
            timer.Stop();
            // Settle on the resting values (the next flash rebuilds the content anyway, but a stopped tween
            // must never leave the board part-faded).
            _dim.Opacity = 1;
            board.Opacity = 1;
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

    // Ease out: fastest at the start, settling at the end. Right for the board's fade-up — most of the
    // opacity arrives early (so the board reads as soon as it's there) without a hard onset.
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

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
