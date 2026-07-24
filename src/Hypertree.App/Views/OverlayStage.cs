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

namespace Hypertree.App.Views;

/// <summary>
/// A single, persistent presentation surface shared by every overlay (see
/// docs/design/overlay-stage.md). Owns one primary-monitor host window plus the per-monitor dim
/// windows — created once, pinned to all virtual desktops once, and shown/hidden rather than
/// created/destroyed. Overlays implement <see cref="IStageContent"/> and are swapped in via
/// <see cref="Present"/>; while the host is already visible a swap is just a content change on it,
/// so there is no tear-down/rebuild flash between modes (and a clean seam for future transitions).
/// </summary>
internal sealed class OverlayStage
{
    private readonly IDesktopController _desktops;
    private readonly IForegroundActivator _activator;

    private StageWindow? _host;
    private readonly List<Window> _dims = new();
    private bool _dimsBuilt;
    private IStageContent? _current;
    private bool _shown;
    private bool _armed; // dismiss-on-deactivate armed only after the foreground dance settles

    public OverlayStage(IDesktopController desktops, IForegroundActivator activator)
    {
        _desktops = desktops;
        _activator = activator;
    }

    public bool IsShowing => _shown;
    public IStageContent? Current => _current;

    /// <summary>The host window handle — the destination for the move picker's DWM thumbnails.</summary>
    public nint HostHandle => _host?.TryGetPlatformHandle()?.Handle ?? 0;
    public double HostScaling => _host?.RenderScaling ?? 1.0;
    public double HostWidth => _host?.Width ?? 0;
    public double HostHeight => _host?.Height ?? 0;

    /// <summary>Screen-relative → host-relative point translation, for positioning DWM thumbnails.</summary>
    public Point PointInHost(Visual v) => (_host is not null ? v.TranslatePoint(new Point(0, 0), _host) : null) ?? default;

    /// <summary>Present <paramref name="content"/>, swapping out whatever is current. If the host is
    /// already shown this is a pure content swap (no flash).</summary>
    public void Present(IStageContent content)
    {
        EnsureHost();

        _current?.OnRemoved();
        _current = content;
        _armed = false;

        _host!.SetContent(content.View, content.Dim);

        if (!_shown)
        {
            _host.Show();
            _shown = true;
            Pin(_host);
        }
        _host.CoverPrimary();
        UpdateDims(content.Dim);

        // Re-assert topmost, force to the foreground (a tray hotkey doesn't grant focus), then let the
        // content take focus / register itself.
        BringToTop();
        if (HostHandle != 0) _activator.ForceForeground(HostHandle);
        _host.Activate();
        _host.Focus(); // window-level key focus for content with no focusable child (map/move)
        content.OnPresented(this);

        Dispatcher.UIThread.Post(() => _armed = true, DispatcherPriority.Background);
    }

    /// <summary>Re-host the current content's (rebuilt) view and re-lift — after a navigation redraw.</summary>
    public void Update(IStageContent content)
    {
        if (_current != content || _host is null) return;
        _host.SetContent(content.View, content.Dim);
        BringToTop();
    }

    /// <summary>Hide the stage (windows stay alive for the next summon). No-op if already hidden.</summary>
    public void Dismiss()
    {
        if (!_shown) return;
        _current?.OnRemoved();
        _current = null;
        foreach (Window d in _dims) d.Hide();
        _host?.Hide();
        _shown = false;
    }

    /// <summary>Re-assert topmost — after a navigation whose desktop switch can surface a foreground
    /// window above the host (content that mutates its view in place, rather than re-presenting).</summary>
    public void BringToFront() => BringToTop();

    /// <summary>Give keyboard focus to a control within the host (e.g. a palette's search box).</summary>
    public void Focus(Control c) => c.Focus();

    /// <summary>Tear the windows down for good (app exit).</summary>
    public void Close()
    {
        foreach (Window d in _dims) d.Close();
        _dims.Clear();
        _host?.Close();
        _host = null;
        _shown = false;
        _current = null;
    }

    private void EnsureHost()
    {
        if (_host is not null) return;
        _host = new StageWindow();
        _host.KeyForwarded += e => _current?.OnKey(e);
        _host.BackgroundPressed += () => { if (_current?.DismissOnClickAway == true) Dismiss(); };
        _host.HostDeactivated += () => { if (_armed && _current?.DismissOnDeactivate == true) Dismiss(); };
    }

    private void UpdateDims(bool dim)
    {
        EnsureDims();
        foreach (Window d in _dims)
        {
            if (dim) d.Show(); else d.Hide();
        }
        if (dim) foreach (Window d in _dims) Pin(d);
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

/// <summary>What the <see cref="OverlayStage"/> presents. A built view plus the small set of policies
/// the stage needs to route input and decide dismissal, and lifecycle hooks for focus/cleanup.</summary>
internal interface IStageContent
{
    /// <summary>The visual to host. May be rebuilt between presents; the stage reads it on present.</summary>
    Control View { get; }

    /// <summary>Whether to show the full dim backdrop (map / preview palettes / move) vs. a transparent
    /// host with just a centred card.</summary>
    bool Dim { get; }

    /// <summary>Dismiss when the host loses focus. True for palettes; false for the map/move, which must
    /// survive the deactivation a desktop switch causes.</summary>
    bool DismissOnDeactivate { get; }

    /// <summary>Dismiss when the backdrop (not the content) is clicked. True for the centred-card
    /// palette (click outside the card closes); false for the map/preview surfaces.</summary>
    bool DismissOnClickAway { get; }

    /// <summary>Called after the view is hosted and the host is foregrounded — take focus, start timers,
    /// register DWM thumbnails, etc. The stage is passed for host handle / focus helpers.</summary>
    void OnPresented(OverlayStage stage);

    /// <summary>Called when this content is swapped out or the stage is dismissed — cleanup.</summary>
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

    private static readonly IBrush DimBg = new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12));

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

        // Backdrop clicks that no content control handled bubble up to here.
        AddHandler(PointerPressedEvent, (_, e) => { if (!e.Handled) BackgroundPressed?.Invoke(); }, RoutingStrategies.Bubble);
        Deactivated += (_, _) => HostDeactivated?.Invoke();
    }

    public void SetContent(Control view, bool dim)
    {
        Background = dim ? DimBg : Brushes.Transparent;
        Content = view;
    }

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
