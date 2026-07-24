using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The two-phase "move windows" overlay, opened on Ctrl+Alt+M. Modelled on <see cref="MapOverlay"/>:
/// dims every screen, covers the primary, and pins all its windows to every desktop so it survives the
/// desktop switches of phase 2. Phase 1 is a Task-View-style grid of the current desktop's windows
/// (live DWM thumbnails) with keyboard multi-select; phase 2 reuses <see cref="BoardView"/> to show the
/// map while the user navigates to a destination, then drops the selected windows there.
///
/// It holds no model: navigation and the actual move are raised as events for <c>App</c> (which owns the
/// <see cref="NavigationModel"/> and <see cref="IDesktopController"/>) to service, mirroring MapOverlay.
/// </summary>
internal sealed class MoveWindowsOverlay
{
    private readonly IDesktopController _desktops;
    private readonly IForegroundActivator _activator;
    private readonly List<Window> _dims = new();
    private MoveWindow? _win;
    private WindowMoveSession? _session;

    public bool IsOpen => _win is not null;

    /// <summary>Phase-1 Enter with a non-empty selection — App builds the board and calls
    /// <see cref="ShowTargeting"/>.</summary>
    public event Action? TargetingEntered;
    /// <summary>Phase-2 arrow key — App applies it to the model and calls <see cref="RefreshBoard"/>.</summary>
    public event Action<NavAction>? NavigateRequested;
    /// <summary>Phase-2 Enter — App moves these windows onto the current desktop and closes.</summary>
    public event Action<IReadOnlyList<nint>>? MoveRequested;
    /// <summary>Esc (either phase) / Backspace (phase 2) — App restores the origin desktop and closes.</summary>
    public event Action? Cancelled;

    public MoveWindowsOverlay(IDesktopController desktops, IForegroundActivator activator)
    {
        _desktops = desktops;
        _activator = activator;
    }

    public void Open(WindowMoveSession session)
    {
        if (IsOpen) return;
        _session = session;

        _win = new MoveWindow(session);
        _win.CancelRequested += () => Cancelled?.Invoke();
        _win.AdvanceRequested += () => TargetingEntered?.Invoke();
        _win.NavigateRequested += a => NavigateRequested?.Invoke(a);
        _win.DropRequested += () => MoveRequested?.Invoke(_session?.SelectedHwnds ?? Array.Empty<nint>());
        _win.Closed += (_, _) => { _win = null; _session = null; };

        _win.Show();
        _win.RenderSelect();

        foreach (Screen s in _win.Screens.All)
        {
            if (_win.Screens.Primary is { } p && s.Bounds == p.Bounds) continue;
            Window dim = MakeDim(s);
            dim.Show();
            _dims.Add(dim);
        }

        _win.Topmost = false;
        _win.Topmost = true;
        Pin(_win);
        foreach (Window d in _dims) Pin(d);

        // Force to the foreground and hand the window key focus — a tray hotkey doesn't grant it.
        if (_win.TryGetPlatformHandle() is { } h) _activator.ForceForeground(h.Handle);
        _win.Activate();
        _win.FocusSelf();
    }

    /// <summary>Switch to phase 2, showing the map board so the user can navigate to a destination.</summary>
    public void ShowTargeting(NavMap map)
    {
        _win?.RenderTargeting(map);
        BringToTop();
    }

    /// <summary>Redraw the phase-2 board after a navigation (the desktop switch can surface a foreground
    /// window above us, so re-lift too — same treatment as MapOverlay.Refresh).</summary>
    public void RefreshBoard(NavMap map)
    {
        _win?.RenderTargeting(map);
        BringToTop();
    }

    public void Close()
    {
        foreach (Window d in _dims) d.Close();
        _dims.Clear();
        _win?.Close();
        _win = null;
        _session = null;
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
        if (_win is not null) Lift(_win);
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
        // Clicking a dimmed (non-primary) screen cancels, like the map's backdrop click.
        dim.PointerPressed += (_, _) => Cancelled?.Invoke();
        return dim;
    }
}

/// <summary>The move overlay's single window: a full-screen, box-less surface on the primary monitor.
/// Phase 1 draws the window-card grid (live thumbnails); phase 2 draws the map board with a banner.
/// Raises intent events; the orchestrator/App own the model and perform the work.</summary>
internal sealed class MoveWindow : Window
{
    public event Action? CancelRequested;
    public event Action? AdvanceRequested;
    public event Action<NavAction>? NavigateRequested;
    public event Action? DropRequested;

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly Color CardStroke = Color.Parse("#2A3444");
    private static readonly Color FocusRing = Color.Parse("#C9D4E5"); // near-white cursor outline (transient)
    private static readonly Color SelBorder = Color.Parse("#4C9AFF"); // strong blue — a selected (ticked) card
    private static readonly Color SelBg = Color.Parse("#182740");     // bluish card fill when selected
    private static readonly Color BodyBg = Color.Parse("#0B0E14");
    private static readonly Color CapBg = Color.Parse("#161C27");
    private static readonly Color BarBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22); // instruction pill background
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // Phase-1 card geometry (DIPs).
    private const double CardW = 252, BodyH = 142, CapH = 34, Gap = 16, BodyPad = 4;

    private readonly WindowMoveSession _session;
    private readonly List<Card> _cards = new();
    private ScrollViewer? _scroll;
    private int _columns = 1;
    private bool _targeting;

    private sealed record Card(Border Outer, Border Body, TextBlock Check, DwmThumbnail? Thumb)
    {
        public DwmThumbnail? Thumb { get; set; } = Thumb;
    }

    public MoveWindow(WindowMoveSession session)
    {
        _session = session;
        Title = "Hypertree — Move windows";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        RequestedThemeVariant = ThemeVariant.Dark;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12));
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // Reposition the live thumbnails whenever layout settles (first real layout, DPI changes).
        LayoutUpdated += (_, _) => { if (!_targeting) PlaceThumbnails(); };
    }

    /// <summary>Give the window itself keyboard focus so plain arrow keys reach <see cref="OnKeyDown"/>.</summary>
    public void FocusSelf() => Focus();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_targeting) OnTargetingKey(e);
        else OnSelectKey(e);
    }

    private void OnSelectKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CancelRequested?.Invoke(); e.Handled = true; break;
            case Key.Left: if (_session.MoveFocus(-1)) Highlight(); e.Handled = true; break;
            case Key.Right: if (_session.MoveFocus(+1)) Highlight(); e.Handled = true; break;
            case Key.Up: if (_session.MoveFocus(-_columns)) Highlight(); e.Handled = true; break;
            case Key.Down: if (_session.MoveFocus(+_columns)) Highlight(); e.Handled = true; break;
            case Key.Space: _session.ToggleSelected(); Highlight(); e.Handled = true; break;
            case Key.Enter:
                if (_session.EnsureFocusedSelected()) { Highlight(); AdvanceRequested?.Invoke(); }
                e.Handled = true;
                break;
        }
    }

    private void OnTargetingKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Back: CancelRequested?.Invoke(); e.Handled = true; break;
            case Key.Left: NavigateRequested?.Invoke(NavAction.MoveLeft); e.Handled = true; break;
            case Key.Right: NavigateRequested?.Invoke(NavAction.MoveRight); e.Handled = true; break;
            case Key.Up: NavigateRequested?.Invoke(NavAction.Surface); e.Handled = true; break;
            case Key.Down: NavigateRequested?.Invoke(NavAction.Dive); e.Handled = true; break;
            case Key.Enter: DropRequested?.Invoke(); e.Handled = true; break;
        }
    }

    // ── Phase 1: the window-card grid ──────────────────────────────────────────────

    public void RenderSelect()
    {
        _targeting = false;
        CoverPrimary();
        DisposeThumbnails();
        _cards.Clear();
        _scroll = null;

        if (_session.IsEmpty)
        {
            Content = new Grid { Children = { HintBar("No windows to move on this desktop · Esc to close") } };
            return;
        }

        // Columns to fit ~92% of the width; rows follow. Up/Down step by exactly this many.
        double avail = Width * 0.92;
        _columns = Math.Max(1, Math.Min(_session.Windows.Count, (int)(avail / (CardW + Gap))));

        var grid = new UniformGrid { Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < _session.Windows.Count; i++)
            grid.Children.Add(BuildCard(_session.Windows[i], i));

        // A lot of windows can overflow the screen — make the grid scrollable, bounded to a band that
        // clears the header. Reposition thumbnails as it scrolls (they're OS-composited, not laid out).
        _scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = Math.Max(200, Height - 160),
            Margin = new Thickness(0, 96, 0, 40),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _scroll.ScrollChanged += (_, _) => { if (!_targeting) PlaceThumbnails(); };

        Border header = HintBar("Select windows to move · ←→↑↓ move · Space tick · Enter choose destination · Esc cancel");
        header.VerticalAlignment = VerticalAlignment.Top;
        header.Margin = new Thickness(0, 24, 0, 0);

        Content = new Grid { Children = { _scroll, header } };
        Highlight();
    }

    private Control BuildCard(WindowInfo w, int index)
    {
        var body = new Border
        {
            Width = CardW - 2 * BodyPad, Height = BodyH,
            Background = new SolidColorBrush(BodyBg),
            CornerRadius = new CornerRadius(6),
        };

        var check = new TextBlock
        {
            Text = "○", FontSize = 15, FontFamily = Mono, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var procText = new TextBlock
        {
            Text = string.IsNullOrEmpty(w.ProcessName) ? w.Title : w.ProcessName,
            FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var caption = new StackPanel
        {
            Orientation = Orientation.Horizontal, Height = CapH, Margin = new Thickness(8, 0),
            Children = { check, procText },
        };

        var outer = new Border
        {
            Width = CardW, Margin = new Thickness(Gap / 2),
            Padding = new Thickness(BodyPad),
            Background = new SolidColorBrush(CapBg),
            BorderBrush = new SolidColorBrush(CardStroke), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(9),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Children = { body, caption } },
        };
        // Click focuses + toggles this card (mouse path mirrors keyboard Space).
        outer.PointerPressed += (_, _) => { _session.MoveFocus(index - _session.Focus); _session.ToggleSelected(); Highlight(); };

        _cards.Add(new Card(outer, body, check, null));
        return outer;
    }

    // Register (once) and position each card's live thumbnail over its reserved body rect, in the
    // window's physical client pixels. Windows that can't be thumbnailed keep a bare body (labelled).
    private void PlaceThumbnails()
    {
        nint dest = TryGetPlatformHandle()?.Handle ?? 0;
        if (dest == 0) return;
        double scale = RenderScaling;

        // The scroll viewport, in window DIP coords — cards outside it are hidden (DWM can't clip).
        double vpTop = 0, vpBottom = Height;
        if (_scroll is not null)
        {
            Point sp = _scroll.TranslatePoint(new Point(0, 0), this) ?? default;
            vpTop = sp.Y;
            vpBottom = sp.Y + _scroll.Bounds.Height;
        }

        for (int i = 0; i < _cards.Count; i++)
        {
            Card c = _cards[i];
            if (c.Body.Bounds.Width < 1 || c.Body.Bounds.Height < 1) continue;

            if (c.Thumb is null)
            {
                var t = new DwmThumbnail(dest, _session.Windows[i].Hwnd);
                c.Thumb = t;
                if (!t.Ok) MarkNoThumbnail(c, _session.Windows[i]);
            }
            if (c.Thumb?.Ok != true) continue;

            Point p = c.Body.TranslatePoint(new Point(0, 0), this) ?? default;
            double top = p.Y, bottom = p.Y + c.Body.Bounds.Height;
            // Only show a card whose body is fully inside the viewport, so partly-scrolled cards don't
            // bleed the (unclippable) thumbnail over the header/edges.
            if (top < vpTop - 0.5 || bottom > vpBottom + 0.5) { c.Thumb.Hide(); continue; }

            int l = (int)Math.Round(p.X * scale), tp = (int)Math.Round(top * scale);
            int r = (int)Math.Round((p.X + c.Body.Bounds.Width) * scale);
            int b = (int)Math.Round(bottom * scale);
            c.Thumb.Place(l, tp, r, b);
        }
    }

    // Fallback for a window DWM won't thumbnail: show its title centred in the body.
    private static void MarkNoThumbnail(Card c, WindowInfo w)
    {
        c.Body.Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(w.Title) ? w.ProcessName : w.Title,
            FontSize = 12, Foreground = FgDim, TextWrapping = TextWrapping.Wrap, MaxWidth = CardW - 24,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // Repaint focus/selection cues without rebuilding cards (so thumbnails stay registered/placed).
    // Two distinct cues: a strong blue border + fill marks a *selected* (ticked) card; a near-white
    // ring + a small lift marks the *focus* cursor (transient). They stack when a card is both.
    private void Highlight()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            Card c = _cards[i];
            bool focused = i == _session.Focus;
            bool selected = _session.IsSelected(i);

            if (selected)
            {
                c.Outer.BorderBrush = new SolidColorBrush(SelBorder);
                c.Outer.BorderThickness = new Thickness(3);
                c.Outer.Background = new SolidColorBrush(SelBg);
            }
            else
            {
                c.Outer.BorderBrush = new SolidColorBrush(focused ? FocusRing : CardStroke);
                c.Outer.BorderThickness = new Thickness(focused ? 2 : 1.5);
                c.Outer.Background = new SolidColorBrush(CapBg);
            }

            c.Outer.RenderTransform = focused ? new TranslateTransform(0, -4) : null;
            c.Check.Text = selected ? "✓" : "○";
            c.Check.Foreground = new SolidColorBrush(selected ? SelBorder : Color.Parse("#9AA6B8"));

            if (focused) c.Outer.BringIntoView(); // keep the cursor visible as it moves through a scroll
        }
    }

    // ── Phase 2: the map board ──────────────────────────────────────────────────────

    public void RenderTargeting(NavMap map)
    {
        _targeting = true;
        CoverPrimary();
        DisposeThumbnails(); // no live previews behind the board
        _scroll = null;

        Control board = BoardView.Render(map, Width, Height, 1.0);

        int n = _session.SelectedCount;
        Border banner = HintBar($"Moving {n} window{(n == 1 ? "" : "s")} · ←→↑↓ navigate · Enter to drop here · Esc/Backspace cancel");
        banner.VerticalAlignment = VerticalAlignment.Top;
        banner.Margin = new Thickness(0, 24, 0, 0);

        Content = new Grid { Children = { board, banner } };
    }

    // A centred instruction pill — a rounded, semi-opaque bar so the text clearly reads as an overlay
    // affordance rather than floating on the dim backdrop.
    private static Border HintBar(string text) => new()
    {
        Background = new SolidColorBrush(BarBg),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 9),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private void DisposeThumbnails()
    {
        foreach (Card c in _cards) { c.Thumb?.Dispose(); c.Thumb = null; }
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeThumbnails();
        base.OnClosed(e);
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
