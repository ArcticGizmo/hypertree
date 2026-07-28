using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// A single, persistent presentation surface shared by every overlay (see
/// docs/design/overlay-stage.md). Owns one primary-monitor host window plus the per-monitor dim
/// windows — created once, pinned to all virtual desktops once, and shown/hidden rather than
/// created/destroyed. Overlays implement <see cref="IStageContent"/> and are pushed on via
/// <see cref="Present"/> / <see cref="Summon"/>; while the host is already visible a change is just a
/// content swap on it, so there is no tear-down/rebuild flash between modes.
///
/// The stage keeps a <b>navigation back-stack</b> (browser-style): <see cref="Summon"/> starts a fresh
/// root, <see cref="Present"/> pushes a sub-surface on top, <see cref="Back"/> pops one step (Esc /
/// Cancel), and <see cref="CompleteToBase"/> unwinds to the durable base — the map — when an action
/// finishes (or dismisses outright if there's no map in the chain). Card-style content floats over a
/// live <b>map backdrop</b> (<see cref="MapProvider"/>), so every non-special view shows the board
/// behind it; full-surface content (the map, the move flow) draws its own board and gets no backdrop.
/// </summary>
internal sealed class OverlayStage
{
    private readonly IDesktopController _desktops;
    private readonly IForegroundActivator _activator;

    private StageWindow? _host;
    private readonly List<Window> _dims = new();
    private bool _dimsBuilt;
    private readonly List<IStageContent> _stack = new(); // top (last) = the surface currently shown
    private IStageContent? _backdropOwner; // whose board the current backdrop belongs to (Card content)
    private bool _shown;
    private bool _armed; // dismiss-on-deactivate armed only after the foreground dance settles

    public OverlayStage(IDesktopController desktops, IForegroundActivator activator)
    {
        _desktops = desktops;
        _activator = activator;
    }

    public bool IsShowing => _shown;
    public IStageContent? Current => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>Whether the current chain has a durable base (the map) — i.e. completing an action will
    /// return there rather than dismiss. App uses this to decide whether to prime the map or flash the HUD.</summary>
    public bool HasDurableBase => _stack.Any(c => c.Durable);

    /// <summary>Supplies the live board rendered behind Card content (App: <c>() =&gt; _model.BuildMap()</c>).</summary>
    public Func<NavMap>? MapProvider;

    /// <summary>The board style for every surface the stage draws (card backdrops here; the map and move
    /// flow read it off the stage too). App keeps this in sync with the persisted setting, so choosing the
    /// metro map applies everywhere at once.</summary>
    public MapStyle MapStyle { get; set; } = MapStyle.Board;

    /// <summary>Raised when the stage becomes visible (first content shown) and when it hides (stack
    /// emptied). App uses these to park the taskbar pill while the overlay is up.</summary>
    public event Action? Shown;
    public event Action? Hidden;

    /// <summary>Realize and size the host (and build the dims) up front — shown transparent+empty, sized to
    /// the primary monitor, then hidden — so the very first real present shows an already-sized surface
    /// instead of briefly rendering at the window's default top-left size. Call once at startup.</summary>
    public void Prewarm()
    {
        EnsureHost();
        _host!.SetContent(null, new Panel(), transparent: true); // transparent + empty: nothing visible while we size it
        _host.Show();
        WindowFx.DisableTransitions(HostHandle);
        Pin(_host);
        _host.CoverPrimary();
        EnsureDims();
        _host.Hide(); // stays realized + sized for the first present; _shown remains false
    }

    /// <summary>The host window handle — the destination for the move picker's DWM thumbnails.</summary>
    public nint HostHandle => _host?.TryGetPlatformHandle()?.Handle ?? 0;
    public double HostScaling => _host?.RenderScaling ?? 1.0;
    public double HostWidth => _host?.Width ?? 0;
    public double HostHeight => _host?.Height ?? 0;

    /// <summary>Screen-relative → host-relative point translation, for positioning DWM thumbnails.</summary>
    public Point PointInHost(Visual v) => (_host is not null ? v.TranslatePoint(new Point(0, 0), _host) : null) ?? default;

    /// <summary>Start a fresh flow: clear the stack and present <paramref name="content"/> as the new root.
    /// Used by the global hotkeys that begin a flow (map, command palette, move).</summary>
    public void Summon(IStageContent content)
    {
        EnsureHost();
        IStageContent[] cleared = _stack.ToArray();
        _stack.Clear();
        foreach (IStageContent c in cleared) c.OnRemoved();
        _stack.Add(content);
        Activate(content);
    }

    /// <summary>Push <paramref name="content"/> on top of the current surface (which stays alive beneath,
    /// its state preserved). Used when a surface opens a sub-surface — Esc pops back to what was underneath.
    /// If the host is already shown this is a pure content swap (no flash).</summary>
    public void Present(IStageContent content)
    {
        EnsureHost();
        _stack.Add(content);
        Activate(content);
    }

    /// <summary>Pop <paramref name="popCount"/> frames (running each one's <see cref="IStageContent.OnRemoved"/>)
    /// then present <paramref name="content"/> in their place — replacing the top of the stack while everything
    /// below is preserved, in a single activation (no flash through the intermediate frames). Used to refresh a
    /// surface pushed-over from a card: e.g. after an action taken on a card returns to a now-stale list, pop the
    /// card and the stale list (popCount 2) and drop a freshly-built list into its slot, so the surface beneath
    /// (the command palette) still catches Esc.</summary>
    public void ReplaceTop(int popCount, IStageContent content)
    {
        EnsureHost();
        for (int i = 0; i < popCount && _stack.Count > 0; i++)
        {
            IStageContent top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            top.OnRemoved();
        }
        _stack.Add(content);
        Activate(content);
    }

    /// <summary>Pop the current surface and return to the one beneath it (Esc / Cancel). Empties the stack
    /// ⇒ hides the stage. This is the browser "back" step.</summary>
    public void Back()
    {
        if (_stack.Count == 0) return;
        IStageContent top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.OnRemoved();
        if (_stack.Count == 0) { Hide(); return; }
        Activate(Current!);
    }

    /// <summary>An action finished: unwind to the durable base (the map) and re-present it, so you land
    /// back where the chain started. If nothing in the chain is durable (a flow summoned from a hotkey),
    /// dismiss outright.</summary>
    public void CompleteToBase()
    {
        int baseIdx = -1;
        for (int i = 0; i < _stack.Count; i++) if (_stack[i].Durable) { baseIdx = i; break; }
        if (baseIdx < 0) { Dismiss(); return; }
        while (_stack.Count - 1 > baseIdx)
        {
            IStageContent top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            top.OnRemoved();
        }
        Activate(Current!);
    }

    /// <summary>Tear the whole stack down and hide (no-op if already hidden). Terminal actions (a jump that
    /// physically moves you) call this directly.</summary>
    public void Dismiss()
    {
        if (!_shown && _stack.Count == 0) return;
        IStageContent[] cleared = _stack.ToArray();
        _stack.Clear();
        foreach (IStageContent c in cleared) c.OnRemoved();
        Hide();
    }

    // Render the given (now-top) frame onto the host and run the foreground dance.
    private void Activate(IStageContent content)
    {
        _armed = false;

        // Size + set the content BEFORE showing (the host is realized and sized from Prewarm), so the very
        // first appearance is already the finished surface — not the empty host for a frame, then the content
        // popping in (which reads as the overlay opening twice).
        _host!.CoverPrimary();
        // Card content floats over a live map backdrop; full-surface content (map / move) draws its own board.
        if (content.Layer == StageLayer.Card)
        {
            _backdropOwner = content;
            _host.SetContent(RenderBackdrop(content), content.View);
        }
        else
        {
            _backdropOwner = null;
            _host.SetContent(null, content.View);
        }

        bool firstShow = !_shown;
        if (firstShow)
        {
            _host.Show();
            _shown = true;
            Pin(_host);
            WindowFx.DisableTransitions(HostHandle); // no DWM scale/fade as the overlay (re)appears
        }
        UpdateDims();

        // Re-assert topmost, force to the foreground (a tray hotkey doesn't grant focus), then let the
        // content take focus / register itself.
        BringToTop();
        if (HostHandle != 0) _activator.ForceForeground(HostHandle);
        _host.Activate();
        _host.Focus(); // window-level key focus for content with no focusable child (map/move)
        content.OnPresented(this);

        Dispatcher.UIThread.Post(() => _armed = true, DispatcherPriority.Background);
        if (firstShow) Shown?.Invoke();
    }

    // The board to paint behind a card: the content's own override (a jump-target highlight, a snapshot
    // preview) or, by default, the live map.
    private Control? RenderBackdrop(IStageContent content)
    {
        NavMap? map = content.BackdropBoard() ?? MapProvider?.Invoke();
        if (map is null) return null;
        double w = HostWidth > 0 ? HostWidth : 1280, h = HostHeight > 0 ? HostHeight : 800;
        return MapSurface.Render(map, w, h, MapStyle);
    }

    /// <summary>Re-render the current card's backdrop board — after its selection moved (palette preview).</summary>
    public void RefreshBackdrop()
    {
        if (_backdropOwner is null) return;
        _host?.SetBackdrop(RenderBackdrop(_backdropOwner));
    }

    private void Hide()
    {
        if (!_shown) return;
        foreach (Window d in _dims) d.Hide();
        _host?.Hide();
        _shown = false;
        _backdropOwner = null;
        Hidden?.Invoke();
    }

    /// <summary>Re-assert topmost — after a navigation whose desktop switch can surface a foreground
    /// window above the host (content that mutates its view in place, rather than re-presenting).</summary>
    public void BringToFront() => BringToTop();

    /// <summary>Re-grab the foreground and window-level key focus for the current content — after a child
    /// window that stole focus (e.g. the Settings window) closes, so the stage's own key handling resumes.
    /// No-op when hidden.</summary>
    public void Reassert()
    {
        if (!_shown || _host is null) return;
        BringToTop();
        if (HostHandle != 0) _activator.ForceForeground(HostHandle);
        _host.Activate();
        _host.Focus();
    }

    /// <summary>Tear the windows down for good (app exit).</summary>
    public void Close()
    {
        foreach (Window d in _dims) d.Close();
        _dims.Clear();
        _host?.Close();
        _host = null;
        _shown = false;
        _stack.Clear();
        _backdropOwner = null;
    }

    private void EnsureHost()
    {
        if (_host is not null) return;
        _host = new StageWindow();
        _host.KeyForwarded += e => Current?.OnKey(e);
        _host.BackgroundPressed += () => { if (Current?.DismissOnClickAway == true) Back(); };
        _host.HostDeactivated += () => { if (_armed && Current?.DismissOnDeactivate == true) Dismiss(); };
    }

    // Card content always dims (the board reads behind it); full-surface content draws its own dim board.
    private void UpdateDims()
    {
        EnsureDims();
        foreach (Window d in _dims)
        {
            d.Show();
            WindowFx.DisableTransitions(d.TryGetPlatformHandle()?.Handle ?? 0);
            Pin(d);
        }
        BringToTop();
    }

    private void EnsureDims()
    {
        if (_dimsBuilt || _host is null) return;
        _dimsBuilt = true;
        foreach (Screen s in _host.Screens.All)
        {
            if (_host.Screens.Primary is { } p && s.Bounds == p.Bounds) continue; // host covers primary
            Window dim = MakeDim(s);
            _dims.Add(dim);
        }
    }

    private void Pin(Window w)
    {
        nint h = w.TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0) { try { _desktops.PinWindow(h); } catch { /* best-effort */ } }
    }

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private void BringToTop()
    {
        foreach (Window d in _dims) Lift(d);
        if (_host is not null) Lift(_host); // host last, above the dims
    }

    private static void Lift(Window w)
    {
        nint h = w.TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private Window MakeDim(Screen s)
    {
        double scale = s.Scaling;
        var dim = new Window
        {
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromArgb(0x82, 0x10, 0x10, 0x10)),
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            CanResize = false, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Position = s.Bounds.Position, Width = s.Bounds.Width / scale, Height = s.Bounds.Height / scale,
        };
        dim.PointerPressed += (_, _) => Dismiss(); // clicking another monitor's dim closes the stage
        return dim;
    }
}

/// <summary>How the <see cref="OverlayStage"/> frames a piece of content.</summary>
internal enum StageLayer
{
    /// <summary>The View covers the whole stage and draws its own board (the map, the move flow). No
    /// separate backdrop.</summary>
    FullSurface,

    /// <summary>A floating card (palettes, prompts, confirm, branch). The stage renders a live map board
    /// behind it.</summary>
    Card,
}

/// <summary>What the <see cref="OverlayStage"/> presents. A built view plus the small set of policies
/// the stage needs to route input and decide dismissal, and lifecycle hooks for focus/cleanup.</summary>
internal interface IStageContent
{
    /// <summary>The visual to host. May be rebuilt between presents; the stage reads it on present.</summary>
    Control View { get; }

    /// <summary>Whether the content fills the stage itself or floats as a card over the map backdrop.</summary>
    StageLayer Layer { get; }

    /// <summary>Dismiss when the host loses focus. True for palettes; false for the map/move/prompts, which
    /// must survive the deactivation a desktop switch causes (and never drop a half-typed name).</summary>
    bool DismissOnDeactivate { get; }

    /// <summary>Step back (pop) when the backdrop (not the content) is clicked. True for the command-list
    /// palettes (click the board to go back); false for prompts and the map/move surfaces.</summary>
    bool DismissOnClickAway { get; }

    /// <summary>The durable base of a flow that completed actions unwind to — only the map. When a chain
    /// has no durable frame, completing an action dismisses the stage.</summary>
    bool Durable => false;

    /// <summary>The board to paint behind this card, or null to use the stage's live map. Full-surface
    /// content ignores this (it draws its own board).</summary>
    NavMap? BackdropBoard() => null;

    /// <summary>Called after the view is hosted and the host is foregrounded — take focus, start timers,
    /// register DWM thumbnails, etc. The stage is passed for host handle / focus helpers.</summary>
    void OnPresented(OverlayStage stage);

    /// <summary>Called when this content is popped off the stack or the stage is dismissed — cleanup.</summary>
    void OnRemoved();

    /// <summary>A key press while this content is current. Set <see cref="KeyEventArgs.Handled"/>.</summary>
    void OnKey(KeyEventArgs e);
}

/// <summary>The stage's single host window: a full-screen, box-less, top-most surface on the primary
/// monitor. Holds no overlay logic — it just hosts a content view, forwards keys and backdrop clicks,
/// and reports deactivation, so the <see cref="OverlayStage"/> can apply each content's policy.</summary>
internal sealed class StageWindow : Window
{
    public event Action<KeyEventArgs>? KeyForwarded;
    public event Action? BackgroundPressed;
    public event Action? HostDeactivated;

    private static readonly IBrush DimBg = BuildDim();

    // The backdrop the board draws over. A soft vignette rather than a flat slab: darker in the centre —
    // where the board (and especially the metro map's thin coloured lines) sits — fading out to the same
    // dim it always was at the edges. Since the host is semi-transparent over the live desktop, a busy or
    // light screen behind used to wash the centre out; pooling the dark under the content fixes the contrast
    // without making the whole overlay heavier. Radii reach past the corners so the outer field is flat.
    internal static IBrush BuildDim() => new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xDD, 0x07, 0x08, 0x0C), 0.0),  // darker pool under the content
            new GradientStop(Color.FromArgb(0xBE, 0x0B, 0x0C, 0x10), 0.55),
            new GradientStop(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12), 1.0),  // = the previous flat dim, at the edges
        },
    };

    // Two persistent layers: the map backdrop below, the content view above. They're ContentControls so
    // swapping a layer detaches the previous child cleanly — no double-parenting when a card is re-presented.
    private readonly ContentControl _backdropSlot = new();
    private readonly ContentControl _contentSlot = new();

    public StageWindow()
    {
        Title = "Hypertree";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        RequestedThemeVariant = ThemeVariant.Dark; // themed controls (palette search box) render dark
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = true;
        Background = DimBg;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Content = new Grid { Children = { _backdropSlot, _contentSlot } };

        // Backdrop clicks that no content control handled bubble up to here.
        AddHandler(PointerPressedEvent, (_, e) => { if (!e.Handled) BackgroundPressed?.Invoke(); }, RoutingStrategies.Bubble);
        Deactivated += (_, _) => HostDeactivated?.Invoke();
    }

    /// <summary>Host a view over an optional backdrop board. Always dimmed (the map backdrop / a full-surface
    /// board reads over it); the transparent flag is only used by Prewarm to size the host invisibly.</summary>
    public void SetContent(Control? backdrop, Control view, bool transparent = false)
    {
        Background = transparent ? Brushes.Transparent : DimBg;
        _backdropSlot.Content = backdrop;
        _contentSlot.Content = view;
    }

    /// <summary>Swap just the backdrop board (a palette repainting its preview as the selection moves).</summary>
    public void SetBackdrop(Control? backdrop) => _backdropSlot.Content = backdrop;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        KeyForwarded?.Invoke(e);
    }

    public void CoverPrimary()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }
}
