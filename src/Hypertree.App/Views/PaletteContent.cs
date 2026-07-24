using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>One filterable row in a palette: a primary <paramref name="Label"/>, an optional dimmer
/// <paramref name="Detail"/> (also folded into the match text, so e.g. a group name filters its
/// desktops), an optional trailing <paramref name="Glyph"/>, the action to run when it's chosen, and —
/// in preview mode — a <paramref name="Preview"/> board to show while it's the selected row.</summary>
internal sealed record PaletteItem(string Label, string? Detail, string? Glyph, Action Choose,
                                   Func<NavMap>? Preview = null, string? DisabledReason = null)
{
    /// <summary>A greyed-out row: still shown and selectable (so the reason can be read), but inert.</summary>
    public bool Enabled => DisabledReason is null;

    public bool Matches(string q) =>
        Label.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// The shared spotlight/command palette, hosted on the <see cref="OverlayStage"/>. A search box takes
/// focus immediately; type to filter (case-insensitive Contains over label + detail), Up/Down or Tab to
/// move, Enter to choose the highlighted row, Esc or click-away to dismiss.
///
/// Two layouts: the default centred card (command lists — a "popover" the stage shows without a dim,
/// dismissed by clicking away), and <b>preview mode</b> (jump / previewed commands) — a full-screen dim
/// surface with the card anchored to the top and a live board rendered below it, re-drawn to highlight
/// the currently-selected desktop so the destination is visible. The window-level concerns (cover the
/// primary, force-foreground, dismiss-on-deactivate) belong to the stage; this class is just the view
/// plus its keyboard/filter behaviour.
/// </summary>
internal sealed class PaletteContent : IStageContent
{
    // Reuse the board's dark palette so it reads as one app.
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush RowSel = new SolidColorBrush(Color.Parse("#232C3C"));
    private static readonly IBrush SearchBg = new SolidColorBrush(Color.Parse("#181D28"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly Func<string, PaletteItem?>? _createRow;
    private readonly bool _previewMode;

    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly List<Control> _rows = new();
    private readonly Border? _previewBorder; // holds the board in preview mode
    private readonly Control _root;
    private List<PaletteItem> _filtered;
    private int _selected;
    private bool _chosen;
    private OverlayStage? _stage;

    public PaletteContent(string placeholder, string footerHint, IReadOnlyList<PaletteItem> items,
                          Func<string, PaletteItem?>? createRow = null, bool previewMode = false)
    {
        _all = items;
        _createRow = createRow;
        _previewMode = previewMode;
        _filtered = items.ToList();

        _search = new TextBox
        {
            PlaceholderText = placeholder,
            Background = SearchBg, Foreground = Ink,
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(11, 11, 0, 0), FontSize = 16, Padding = new Thickness(14, 12),
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        _list = new StackPanel { Margin = new Thickness(6) };
        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = previewMode ? 300 : 380,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var hint = new TextBlock
        {
            Text = footerHint, Foreground = Muted, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var footer = new Border
        {
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9), Child = hint,
        };

        var card = new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = new StackPanel { Children = { _search, scroll, footer } }, ClipToBounds = true,
            Width = 560,
        };
        // Clicks on the card must not bubble to the stage backdrop (which would dismiss the popover).
        card.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);

        if (previewMode)
        {
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(0, 44, 0, 0);

            _previewBorder = new Border { Margin = new Thickness(0, 0, 0, 24) };
            _previewBorder.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) UpdatePreview(); };

            DockPanel.SetDock(card, Dock.Top);
            _root = new DockPanel { Children = { card, _previewBorder } };
        }
        else
        {
            // Centred card popover; the stage host is transparent (no dim) and dismisses on click-away.
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            _root = card;
        }

        // Tunnel so Up/Down/Tab/Enter/Esc win before the TextBox consumes them.
        _root.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Rebuild();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public bool Dim => _previewMode;
    public bool DismissOnDeactivate => true;
    public bool DismissOnClickAway => !_previewMode;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _search.Focus();
        if (_previewMode) UpdatePreview();
    }

    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e) { } // handled by the tunneling handler on _root

    // ── Behaviour (unchanged from the old window) ──────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _stage?.Dismiss();
                e.Handled = true;
                break;
            case Key.Down or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_filtered.Count > 0) Choose(_filtered[_selected]);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        int n = _filtered.Count;
        _selected = ((_selected + delta) % n + n) % n;
        Highlight();
    }

    private void ApplyFilter()
    {
        string q = (_search.Text ?? "").Trim();
        _filtered = q.Length == 0 ? _all.ToList() : _all.Where(i => i.Matches(q)).ToList();

        if (_createRow is not null && q.Length > 0
            && !_all.Any(i => i.Label.Equals(q, StringComparison.OrdinalIgnoreCase))
            && _createRow(q) is { } create)
        {
            _filtered.Add(create);
        }

        _selected = 0;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();

        if (_filtered.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No matches", Foreground = Muted, FontSize = 13, Margin = new Thickness(12, 14),
            });
            UpdatePreview();
            return;
        }

        for (int i = 0; i < _filtered.Count; i++)
        {
            var row = BuildRow(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    private Control BuildRow(PaletteItem item, int index)
    {
        bool enabled = item.Enabled;
        var label = new TextBlock
        {
            Text = item.Label, Foreground = enabled ? Ink : Muted, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);

        var detail = new TextBlock
        {
            Text = item.Detail ?? "", Foreground = Muted, FontSize = 12, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(detail, 1);

        var glyph = new TextBlock
        {
            Text = item.Glyph ?? "", Foreground = Accent, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
        };
        Grid.SetColumn(glyph, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(label);
        grid.Children.Add(detail);
        grid.Children.Add(glyph);

        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 9),
            Background = Brushes.Transparent,
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.No),
        };
        border.PointerEntered += (_, _) => { _selected = index; Highlight(); };
        border.PointerPressed += (_, _) => Choose(item); // no-op for disabled rows (guarded in Choose)
        return border;
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i] is Border b)
                b.Background = i == _selected ? RowSel : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].BringIntoView();
        UpdatePreview();
    }

    // Preview mode: draw the selected row's board into the middle region (highlighting the target).
    private void UpdatePreview()
    {
        if (!_previewMode || _previewBorder is null) return;
        Size sz = _previewBorder.Bounds.Size;
        if (sz.Width < 10 || sz.Height < 10) return;

        PaletteItem? sel = _selected >= 0 && _selected < _filtered.Count ? _filtered[_selected] : null;
        NavMap? map = sel?.Preview?.Invoke();
        _previewBorder.Child = map is null ? null : BoardView.Render(map, sz.Width, sz.Height);
    }

    private void Choose(PaletteItem item)
    {
        if (!item.Enabled) return; // greyed-out row: selectable so its reason can be read, but inert
        if (_chosen) return;
        _chosen = true;
        item.Choose();
        // Dismiss the stage only if the action didn't already swap in new content (e.g. "Open map").
        if (_stage?.Current == this) _stage.Dismiss();
    }
}
