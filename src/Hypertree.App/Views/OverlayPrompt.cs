using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>
/// Base for the app's input prompts (name a snapshot/template, define a branch, confirm a delete).
/// Unlike an ordinary dialog — which lives on one desktop and is easily lost the moment you get pulled
/// to another screen — an overlay prompt is a full-screen dim surface on the primary monitor that
/// (a) force-foregrounds on open, (b) is PINNED to every virtual desktop so a desktop switch can't
/// strand it, and (c) keeps re-asserting topmost so a window surfacing underneath (e.g. after
/// navigating) can't bury it. It stays put until you submit (Enter), Cancel, or press Esc — it never
/// dismisses on lost focus or a background click, so a half-typed name can't vanish out from under you.
/// Mirrors the persistence of <see cref="MapOverlay"/>; subclasses just build a card and call
/// <see cref="SetCard"/>.
/// </summary>
internal abstract class OverlayPrompt : Window
{
    // Matches the map's dim + card palette so prompts read as the same surface family.
    private static readonly IBrush DimBackdrop = new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12));
    protected static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    protected static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    protected static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly IForegroundActivator _activator;
    private readonly IDesktopController _desktops;
    private readonly DispatcherTimer _relift;

    protected OverlayPrompt(IForegroundActivator activator, IDesktopController desktops)
    {
        _activator = activator;
        _desktops = desktops;

        WindowDecorations = WindowDecorations.None;
        RequestedThemeVariant = ThemeVariant.Dark; // else the themed inputs render light
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = DimBackdrop;

        // Navigating (or any window surfacing) after the prompt is open can shove it down the z-order;
        // re-assert topmost on a slow tick — non-activating, so it never steals the caret while you type.
        _relift = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _relift.Tick += (_, _) => BringToTop();

        Opened += (_, _) => OnShown();
        Closed += (_, _) => _relift.Stop();
    }

    /// <summary>The control to focus when the overlay opens (usually the primary text box).</summary>
    protected abstract Control? InitialFocus { get; }

    /// <summary>Centre the prompt's card on the dim surface. Subclasses call this from their ctor.</summary>
    protected void SetCard(Control card)
    {
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        Content = new Grid { Children = { card } };
    }

    private void OnShown()
    {
        CoverPrimary();
        nint h = TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0)
        {
            WindowFx.DisableTransitions(h);          // snap in — no DWM scale/fade from the corner
            _activator.ForceForeground(h);           // summoned from a background tray — grab foreground
            try { _desktops.PinWindow(h); } catch { /* best-effort — losing the pin isn't fatal */ }
        }
        Activate();
        BringToTop();
        InitialFocus?.Focus();
        _relift.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    // ── Stay-on-top plumbing (mirrors MapOverlay / HudWindow) ──────────────────────
    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // Re-assert the top of the always-on-top band without stealing focus.
    private void BringToTop()
    {
        nint h = TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void CoverPrimary()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }
}
