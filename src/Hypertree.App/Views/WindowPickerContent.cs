using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hypertree.Desktops;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The shared window-picker surface behind both "move windows" and "pull windows": a Task-View-style grid
/// of live DWM thumbnails with a type-to-filter search box and keyboard/mouse multi-select. It holds no
/// model — selection lives in <see cref="WindowMoveSession"/>, and confirming the pick is deferred to the
/// subclass via <see cref="ConfirmSelection"/> (move enters a destination-picking phase; pull drops the
/// windows here and now).
///
/// A focused search box takes the keystrokes; a tunnelling handler lets the grid keys (arrows / Space /
/// Enter / Esc) win before the box consumes them, so you can filter and navigate at once. Thumbnails are
/// placed in the host window's physical pixels over each card's body rect and hidden when scrolled out of
/// view (DWM can't clip them).
/// </summary>
internal abstract class WindowPickerContent : IStageContent
{
    protected static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    protected static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly Color CardStroke = Color.Parse("#2A3444");
    private static readonly Color FocusRing = Color.Parse("#C9D4E5"); // near-white cursor outline (transient)
    private static readonly Color SelBorder = Color.Parse("#4C9AFF"); // strong blue — a selected (ticked) card
    private static readonly Color SelBg = Color.Parse("#182740");     // bluish card fill when selected
    private static readonly Color BodyBg = Color.Parse("#0B0E14");
    private static readonly Color CapBg = Color.Parse("#161C27");
    private static readonly Color SearchBg = Color.Parse("#181D28");
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A); // legend keycap chip
    protected static readonly Color BarBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22); // instruction pill background
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // Base card geometry (DIPs, at 100% zoom) — deliberately large so near-identical windows (ten terminals)
    // are tellable apart. The card grows/shrinks with the zoom factor; the padding stays put.
    private const double BaseCardW = 340, BaseBodyH = 196, Gap = 18, BodyPad = 4;
    private const double CapH = 34; // caption bar height — a fixed label strip, so it doesn't scale with zoom

    // Thumbnail zoom (Ctrl+ / Ctrl−). A whole-card scale so near-identical windows are easier to tell apart
    // when the default cards are too small to read. Stepped multiplicatively and clamped to a legible range;
    // seeded from the persisted preference and raised back on change so App writes it to settings.json — the
    // picker reopens at the size you left it.
    private double _zoom = 1.0;
    private const double MinZoom = 0.6, MaxZoom = 2.5, ZoomStep = 1.15;

    private double CardW => BaseCardW * _zoom;
    private double BodyH => BaseBodyH * _zoom;

    protected readonly WindowMoveSession Session;
    protected readonly Grid Root = new();
    private readonly List<Card> _cards = new();
    private ScrollViewer? _scroll;
    private TextBox? _search;
    private TextBlock? _zoomLabel; // the "100%" readout in the top-left zoom legend
    protected OverlayStage? Stage;
    private int _columns = 1;
    private bool _pickerActive = true; // false once a subclass takes the surface over (e.g. move's phase 2)

    /// <summary>Ctrl+ / Ctrl− (Ctrl+0 to reset) changed the thumbnail zoom. Carries the new (clamped) factor;
    /// App persists it to settings.json so the picker reopens at the same size.</summary>
    public event Action<double>? ZoomChanged;

    protected WindowPickerContent(WindowMoveSession session, double initialZoom = 1.0)
    {
        Session = session;
        _zoom = Math.Clamp(initialZoom, MinZoom, MaxZoom);
        Root.LayoutUpdated += (_, _) => { if (_pickerActive) PlaceThumbnails(); };
        // Tunnel so the grid keys win before the focused search box consumes them.
        Root.AddHandler(InputElement.KeyDownEvent, OnPickerPreviewKey, RoutingStrategies.Tunnel);
    }

    // ── Subclass contract ──────────────────────────────────────────────────────────

    /// <summary>The instruction line under the search box (grid-key legend).</summary>
    protected abstract string PickerHint { get; }
    /// <summary>Shown when there are no candidate windows at all.</summary>
    protected abstract string EmptyHint { get; }
    /// <summary>Whether a card's caption names the desktop the window is on (pull spans desktops; move doesn't).</summary>
    protected virtual bool ShowSource => false;
    /// <summary>Enter with ≥1 window ticked — the subclass commits or advances.</summary>
    protected abstract void ConfirmSelection();

    /// <summary>Leave the picker for good (a subclass phase takes over): drop the thumbnails and stop the
    /// layout hook from re-placing them. The caller then repopulates <see cref="Root"/>.</summary>
    protected void LeavePicker()
    {
        _pickerActive = false;
        DisposeThumbnails();
        _scroll = null;
        _search = null;
        _zoomLabel = null;
    }

    protected bool PickerActive => _pickerActive;

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => Root;
    public StageLayer Layer => StageLayer.FullSurface; // draws its own thumbnails over the stage's dim
    public bool DismissOnDeactivate => false; // survive the deactivation a stage swap / desktop switch causes
    public bool DismissOnClickAway => false;  // primary clicks don't cancel; a dim-monitor click does

    public virtual void OnPresented(OverlayStage stage)
    {
        Stage = stage;
        RenderPicker();
    }

    public virtual void OnRemoved() => DisposeThumbnails();

    // Grid keys arrive through the tunnelling handler (so they beat the search box); a subclass that grows a
    // second phase without a text box overrides this to handle that phase's keys.
    public virtual void OnKey(KeyEventArgs e) { }

    private void OnPickerPreviewKey(object? sender, KeyEventArgs e)
    {
        if (!_pickerActive) return; // a subclass phase owns the surface — let its keys through to OnKey
        switch (e.Key)
        {
            // Ctrl+ / Ctrl− scale the thumbnails; Ctrl+0 resets to 100%. '+' usually arrives as Shift+OemPlus,
            // so accept OemPlus/Add (and OemMinus/Subtract) and just require Ctrl to be down.
            case Key.Add or Key.OemPlus when e.KeyModifiers.HasFlag(KeyModifiers.Control): Zoom(ZoomStep); e.Handled = true; break;
            case Key.Subtract or Key.OemMinus when e.KeyModifiers.HasFlag(KeyModifiers.Control): Zoom(1 / ZoomStep); e.Handled = true; break;
            case Key.D0 or Key.NumPad0 when e.KeyModifiers.HasFlag(KeyModifiers.Control): ResetZoom(); e.Handled = true; break;
            case Key.Escape: Stage?.Back(); e.Handled = true; break; // return to the map if we opened over it, else hide
            case Key.Left: if (Session.MoveFocus(-1)) Highlight(); e.Handled = true; break;
            case Key.Right: if (Session.MoveFocus(+1)) Highlight(); e.Handled = true; break;
            case Key.Up: if (Session.MoveFocus(-_columns)) Highlight(); e.Handled = true; break;
            case Key.Down: if (Session.MoveFocus(+_columns)) Highlight(); e.Handled = true; break;
            case Key.Space: Session.ToggleSelected(); Highlight(); e.Handled = true; break;
            case Key.Enter:
                if (Session.EnsureFocusedSelected()) { Highlight(); ConfirmSelection(); }
                e.Handled = true;
                break;
        }
    }

    // ── Thumbnail zoom (Ctrl+ / Ctrl− / Ctrl+0) ─────────────────────────────────────
    // Scale the cards, clamped to a legible range, then rebuild the grid at the new size. The layout hook
    // re-places the thumbnails once the new geometry settles. Persisted via App off ZoomChanged.

    private void Zoom(double factor)
    {
        double next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        // The multiplicative steps don't line up with 100%, so a step that crosses it snaps to exactly 1.0 —
        // otherwise you could never get back to the default size once you'd stepped off it.
        if ((_zoom < 1.0 && next > 1.0) || (_zoom > 1.0 && next < 1.0)) next = 1.0;
        if (Math.Abs(next - _zoom) < 1e-6) return; // already at the limit — nothing to redo
        _zoom = next;
        BuildGrid();
        if (_zoomLabel is not null) _zoomLabel.Text = ZoomText();
        ZoomChanged?.Invoke(_zoom);
    }

    private void ResetZoom()
    {
        if (Math.Abs(_zoom - 1.0) < 1e-6) return;
        _zoom = 1.0;
        BuildGrid();
        if (_zoomLabel is not null) _zoomLabel.Text = ZoomText();
        ZoomChanged?.Invoke(_zoom);
    }

    private string ZoomText() => $"{Math.Round(_zoom * 100)}%";

    // A small legend pinned top-left: the thumbnails can be tiny for near-identical windows, so surface the
    // zoom keys and the current scale. Clicking it never falls through to a card behind.
    private Control BuildZoomLegend()
    {
        _zoomLabel = new TextBlock
        {
            Text = ZoomText(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var legend = new Border
        {
            Background = new SolidColorBrush(BarBg),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 24, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    KeyCap("Ctrl"),
                    new TextBlock { Text = "+ / −", FontSize = 11, FontFamily = Mono, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "zoom windows", FontSize = 11, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center },
                    _zoomLabel,
                },
            },
        };
        legend.PointerPressed += (_, e) => e.Handled = true;
        return legend;
    }

    private static Control KeyCap(string key) => new Border
    {
        Background = new SolidColorBrush(KeyCapBg),
        CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent, FontFamily = Mono },
    };

    // ── The window-card grid ────────────────────────────────────────────────────────

    private void RenderPicker()
    {
        _pickerActive = true;
        DisposeThumbnails();
        _cards.Clear();
        Root.Children.Clear();

        if (Session.IsEmpty)
        {
            Root.Children.Add(HintBar(EmptyHint));
            return;
        }

        _search = new TextBox
        {
            PlaceholderText = "Search windows…",
            Background = new SolidColorBrush(SearchBg), Foreground = Fg,
            BorderBrush = new SolidColorBrush(CardStroke), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(11), FontSize = 15, Padding = new Thickness(14, 10),
            Width = 520, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        var hint = new TextBlock
        {
            Text = PickerHint, Foreground = FgDim, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var header = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 20, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _search, hint },
        };
        DockPanel.SetDock(header, Dock.Top);

        _scroll = new ScrollViewer
        {
            MaxHeight = Math.Max(200, (Stage?.HostHeight ?? 800) - 180),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _scroll.ScrollChanged += (_, _) => { if (_pickerActive) PlaceThumbnails(); };

        var dock = new DockPanel { LastChildFill = true, Children = { header, _scroll } };
        Root.Children.Add(dock);
        Root.Children.Add(BuildZoomLegend()); // top-left: the Ctrl+/Ctrl− zoom keys + current scale

        BuildGrid();

        // Focus after the tree is attached, so typing filters immediately while the grid keys still tunnel past.
        Dispatcher.UIThread.Post(() => _search?.Focus());
    }

    private void ApplyFilter()
    {
        if (Session.SetFilter(_search?.Text)) BuildGrid();
    }

    // (Re)build just the card grid inside the persistent scroll/search chrome — the search box keeps its
    // focus and text across a filter change.
    private void BuildGrid()
    {
        if (_scroll is null) return;
        DisposeThumbnails();
        _cards.Clear();

        var list = Session.Visible;
        if (list.Count == 0)
        {
            _scroll.Content = new TextBlock
            {
                Text = $"No windows match “{Session.Filter}”",
                Foreground = FgDim, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            };
            return;
        }

        double avail = (Stage?.HostWidth ?? 1280) * 0.92;
        _columns = Math.Max(1, Math.Min(list.Count, (int)(avail / (CardW + Gap))));

        var grid = new UniformGrid { Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < list.Count; i++)
            grid.Children.Add(BuildCard(list[i], i));

        _scroll.Content = grid;
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
        // A dim "· <desktop>" suffix names where this window lives — the disambiguation the pull picker needs
        // (its cards span desktops) and the move picker doesn't (every card is on the origin).
        if (ShowSource && !string.IsNullOrEmpty(w.DesktopName))
            caption.Children.Add(new TextBlock
            {
                Text = $"· {w.DesktopName}", FontSize = 11, Foreground = FgDim,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 0, 0),
            });

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
        outer.PointerPressed += (_, e) => { e.Handled = true; Session.MoveFocus(index - Session.Focus); Session.ToggleSelected(); Highlight(); };

        _cards.Add(new Card(outer, body, check, null));
        return outer;
    }

    // Register (once) and position each card's live thumbnail over its reserved body rect, in the host
    // window's physical client pixels. Cards outside the scroll viewport are hidden (DWM can't clip).
    private void PlaceThumbnails()
    {
        // Only ever (re)create/position thumbnails while we're the live picker surface. A stray LayoutUpdated
        // during teardown must not re-register DWM thumbnails after DisposeThumbnails — they'd outlive us and
        // paint over whatever shows next.
        if (Stage is null || Stage.Current != this || !_pickerActive) return;
        nint dest = Stage.HostHandle;
        if (dest == 0) return;
        double scale = Stage.HostScaling;

        double vpTop = 0, vpBottom = Stage.HostHeight;
        if (_scroll is not null)
        {
            Point sp = Stage.PointInHost(_scroll);
            vpTop = sp.Y;
            vpBottom = sp.Y + _scroll.Bounds.Height;
        }

        var list = Session.Visible;
        for (int i = 0; i < _cards.Count && i < list.Count; i++)
        {
            Card c = _cards[i];
            if (c.Body.Bounds.Width < 1 || c.Body.Bounds.Height < 1) continue;

            if (c.Thumb is null)
            {
                var t = new DwmThumbnail(dest, list[i].Hwnd);
                c.Thumb = t;
                if (!t.Ok) MarkNoThumbnail(c, list[i]);
            }
            if (c.Thumb?.Ok != true) continue;

            Point p = Stage.PointInHost(c.Body);
            double top = p.Y, bottom = p.Y + c.Body.Bounds.Height;
            if (top < vpTop - 0.5 || bottom > vpBottom + 0.5) { c.Thumb.Hide(); continue; }

            int l = (int)Math.Round(p.X * scale), tp = (int)Math.Round(top * scale);
            int r = (int)Math.Round((p.X + c.Body.Bounds.Width) * scale);
            int b = (int)Math.Round(bottom * scale);
            c.Thumb.Place(l, tp, r, b);
        }
    }

    private void MarkNoThumbnail(Card c, WindowInfo w)
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
            bool focused = i == Session.Focus;
            bool selected = Session.IsSelected(i);

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

    protected static Border HintBar(string text) => new()
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
