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
    private static readonly Color CardStroke = Color.Parse("#2A3444");
    private static readonly Color FocusRing = Color.Parse("#C9D4E5"); // near-white cursor outline (transient)
    private static readonly Color SelBorder = Color.Parse("#4C9AFF"); // strong blue — a selected (ticked) card
    private static readonly Color SelBg = Color.Parse("#182740");     // bluish card fill when selected
    private static readonly Color BodyBg = Color.Parse("#0B0E14");
    private static readonly Color CapBg = Color.Parse("#161C27");
    private static readonly Color SearchBg = Color.Parse("#181D28");
    protected static readonly Color BarBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22); // instruction pill background
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    // Card geometry (DIPs) — deliberately large so near-identical windows (ten terminals) are tellable apart.
    private const double CardW = 340, BodyH = 196, CapH = 34, Gap = 18, BodyPad = 4;

    protected readonly WindowMoveSession Session;
    protected readonly Grid Root = new();
    private readonly List<Card> _cards = new();
    private ScrollViewer? _scroll;
    private TextBox? _search;
    protected OverlayStage? Stage;
    private int _columns = 1;
    private bool _pickerActive = true; // false once a subclass takes the surface over (e.g. move's phase 2)

    protected WindowPickerContent(WindowMoveSession session)
    {
        Session = session;
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
