using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The two-phase "move windows" flow, hosted on the shared <see cref="OverlayStage"/>. Phase 1 is a
/// Task-View-style grid of the current desktop's windows (live DWM thumbnails) with keyboard/mouse
/// multi-select; phase 2 reuses <see cref="BoardView"/> to show the map while the user navigates to a
/// destination, then drops the selected windows there. Both phases mutate one persistent root, so the
/// summon and the phase-1→phase-2 change are content swaps on the already-shown stage — no flash.
///
/// It holds no model: navigation and the move itself are raised as events for <c>App</c> (which owns the
/// <see cref="NavigationModel"/> and desktop controller); the board is pulled via <see cref="BoardProvider"/>.
/// </summary>
internal sealed class MoveContent : IStageContent
{
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
    private readonly Grid _root = new();
    private readonly List<Card> _cards = new();
    private ScrollViewer? _scroll;
    private OverlayStage? _stage;
    private int _columns = 1;
    private bool _targeting;
    private bool _completed; // a successful drop — so OnRemoved doesn't fire the cancel path

    /// <summary>Supplies the board for phase 2 (App: the live map centred on the move's origin).</summary>
    public Func<NavMap>? BoardProvider;
    /// <summary>A phase-2 arrow — App applies it to the model; we then re-pull the board.</summary>
    public event Action<NavAction>? NavigateRequested;
    /// <summary>Phase-2 Enter — App moves these windows onto the current desktop.</summary>
    public event Action<IReadOnlyList<nint>>? MoveRequested;
    /// <summary>Dismissed without dropping (Esc / Backspace / click-away) — App restores the origin.</summary>
    public event Action? Cancelled;

    public MoveContent(WindowMoveSession session)
    {
        _session = session;
        _root.LayoutUpdated += (_, _) => { if (!_targeting) PlaceThumbnails(); };
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface; // draws its own thumbnails / board over the stage's dim
    public bool DismissOnDeactivate => false; // survive the deactivation a desktop switch causes
    public bool DismissOnClickAway => false;  // primary clicks don't cancel; a dim-monitor click does

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        RenderSelect();
    }

    public void OnRemoved()
    {
        DisposeThumbnails();
        if (!_completed) Cancelled?.Invoke(); // Esc / click-away / re-press → restore the origin
    }

    public void OnKey(KeyEventArgs e)
    {
        if (_targeting) OnTargetingKey(e);
        else OnSelectKey(e);
    }

    private void OnSelectKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: _stage?.Back(); e.Handled = true; break; // return to the map if we opened over it, else hide
            case Key.Left: if (_session.MoveFocus(-1)) Highlight(); e.Handled = true; break;
            case Key.Right: if (_session.MoveFocus(+1)) Highlight(); e.Handled = true; break;
            case Key.Up: if (_session.MoveFocus(-_columns)) Highlight(); e.Handled = true; break;
            case Key.Down: if (_session.MoveFocus(+_columns)) Highlight(); e.Handled = true; break;
            case Key.Space: _session.ToggleSelected(); Highlight(); e.Handled = true; break;
            case Key.Enter:
                if (_session.EnsureFocusedSelected()) { Highlight(); EnterTargeting(); }
                e.Handled = true;
                break;
        }
    }

    private void OnTargetingKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Back: _stage?.Back(); e.Handled = true; break; // cancel → back to the map (or hide)
            case Key.Left: Navigate(NavAction.MoveLeft); e.Handled = true; break;
            case Key.Right: Navigate(NavAction.MoveRight); e.Handled = true; break;
            case Key.Up: Navigate(NavAction.Surface); e.Handled = true; break;
            case Key.Down: Navigate(NavAction.Dive); e.Handled = true; break;
            case Key.Enter:
                _completed = true;
                MoveRequested?.Invoke(_session.SelectedHwnds);
                _stage?.CompleteToBase(); // unwind to the map if we opened over it, else dismiss to the desktop
                e.Handled = true;
                break;
        }
    }

    // Apply the navigation through App (which owns the model), then redraw from the fresh board.
    private void Navigate(NavAction a)
    {
        NavigateRequested?.Invoke(a);
        RenderTargeting();
    }

    // ── Phase 1: the window-card grid ──────────────────────────────────────────────

    private void RenderSelect()
    {
        _targeting = false;
        DisposeThumbnails();
        _cards.Clear();
        _scroll = null;
        _root.Children.Clear();

        if (_session.IsEmpty)
        {
            _root.Children.Add(HintBar("No windows to move on this desktop · Esc to close"));
            return;
        }

        double width = _stage?.HostWidth ?? 1280, height = _stage?.HostHeight ?? 800;

        // Columns to fit ~92% of the width; rows follow. Up/Down step by exactly this many.
        double avail = width * 0.92;
        _columns = Math.Max(1, Math.Min(_session.Windows.Count, (int)(avail / (CardW + Gap))));

        var grid = new UniformGrid { Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < _session.Windows.Count; i++)
            grid.Children.Add(BuildCard(_session.Windows[i], i));

        _scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = Math.Max(200, height - 160),
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

        _root.Children.Add(_scroll);
        _root.Children.Add(header);
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
        outer.PointerPressed += (_, e) => { e.Handled = true; _session.MoveFocus(index - _session.Focus); _session.ToggleSelected(); Highlight(); };

        _cards.Add(new Card(outer, body, check, null));
        return outer;
    }

    // Register (once) and position each card's live thumbnail over its reserved body rect, in the host
    // window's physical client pixels. Cards outside the scroll viewport are hidden (DWM can't clip).
    private void PlaceThumbnails()
    {
        // Only ever (re)create/position thumbnails while we're the live surface. A stray LayoutUpdated during
        // teardown (Esc/cancel) must not re-register DWM thumbnails after DisposeThumbnails — they'd outlive
        // us and paint over whatever shows next (e.g. the command palette).
        if (_stage is null || _stage.Current != this) return;
        nint dest = _stage.HostHandle;
        if (dest == 0) return;
        double scale = _stage.HostScaling;

        double vpTop = 0, vpBottom = _stage.HostHeight;
        if (_scroll is not null)
        {
            Point sp = _stage.PointInHost(_scroll);
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

            Point p = _stage.PointInHost(c.Body);
            double top = p.Y, bottom = p.Y + c.Body.Bounds.Height;
            if (top < vpTop - 0.5 || bottom > vpBottom + 0.5) { c.Thumb.Hide(); continue; }

            int l = (int)Math.Round(p.X * scale), tp = (int)Math.Round(top * scale);
            int r = (int)Math.Round((p.X + c.Body.Bounds.Width) * scale);
            int b = (int)Math.Round(bottom * scale);
            c.Thumb.Place(l, tp, r, b);
        }
    }

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

    // Two distinct cues: a strong blue border + fill marks a selected (ticked) card; a near-white ring +
    // a small lift marks the focus cursor (transient). They stack when a card is both.
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

            if (focused) c.Outer.BringIntoView();
        }
    }

    // ── Phase 2: the map board ──────────────────────────────────────────────────────

    private void EnterTargeting()
    {
        _targeting = true;
        DisposeThumbnails(); // no live previews behind the board
        _scroll = null;
        RenderTargeting();
    }

    private void RenderTargeting()
    {
        NavMap? map = BoardProvider?.Invoke();
        if (map is null) return;

        double width = _stage?.HostWidth ?? 1280, height = _stage?.HostHeight ?? 800;
        Control board = BoardView.Render(map, width, height, 1.0);

        int n = _session.SelectedCount;
        Border banner = HintBar($"Moving {n} window{(n == 1 ? "" : "s")} · ←→↑↓ navigate · Enter to drop here · Esc/Backspace cancel");
        banner.VerticalAlignment = VerticalAlignment.Top;
        banner.Margin = new Thickness(0, 24, 0, 0);

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(banner);

        // Navigating switched desktops, which can surface that desktop's foreground window above the
        // pinned host — re-lift so the board stays visible (mirrors MapOverlay.Refresh via Update).
        _stage?.BringToFront();
    }

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

    private sealed record Card(Border Outer, Border Body, TextBlock Check, DwmThumbnail? Thumb)
    {
        public DwmThumbnail? Thumb { get; set; } = Thumb;
    }
}
