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
/// <paramref name="Detail"/> (also folded into the match text, so e.g. a branch name filters its
/// desktops), an optional trailing <paramref name="Glyph"/>, the action to run when it's chosen, and an
/// optional <paramref name="Preview"/> board to show behind the palette while it's the selected row.</summary>
internal sealed record PaletteItem(string Label, string? Detail, string? Glyph, Action Choose,
                                   Func<NavMap>? Preview = null, string? DisabledReason = null,
                                   Action? OnDelete = null)
{
    /// <summary>A greyed-out row: still shown and selectable (so the reason can be read), but inert.</summary>
    public bool Enabled => DisabledReason is null;

    public bool Matches(string q) =>
        Label.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// The shared spotlight/command palette, hosted as a <b>card</b> on the <see cref="OverlayStage"/>. A
/// search box takes focus immediately; type to filter (case-insensitive Contains over label + detail),
/// Up/Down or Tab to move, Enter to choose the highlighted row, Esc or a click on the board to step back.
///
/// The card floats near the top of the stage over the live map backdrop (the stage renders it); as the
/// selection moves, a row's <see cref="PaletteItem.Preview"/> board is shown behind instead — a
/// jump-target highlight, a command's target, a snapshot's layout — via <see cref="BackdropBoard"/> +
/// <see cref="OverlayStage.RefreshBackdrop"/>. Rows with no preview fall back to the live map. The
/// window-level concerns (cover the primary, force-foreground, dismiss-on-deactivate) belong to the
/// stage; this class is just the view plus its keyboard/filter behaviour.
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
    private readonly bool _clearSearchOnShow;

    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly List<Control> _rows = new();
    private readonly Control _root;
    private List<PaletteItem> _filtered;
    private int _selected;
    private bool _chosen;
    private OverlayStage? _stage;

    /// <param name="clearSearchOnShow">Reset the filter every time the palette is (re)shown. Only visible on
    /// a pop-back — you dive into a row's sub-surface then Esc back — so you land on the full, unfiltered list
    /// rather than your old query. Off by default (other palettes keep their filter when you return).</param>
    public PaletteContent(string placeholder, string footerHint, IReadOnlyList<PaletteItem> items,
                          Func<string, PaletteItem?>? createRow = null, bool clearSearchOnShow = false)
    {
        _all = items;
        _createRow = createRow;
        _clearSearchOnShow = clearSearchOnShow;
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
            Content = _list, MaxHeight = 340,
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
            // Anchored near the top so the map backdrop the stage draws stays visible below/around it.
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 0, 0),
        };
        // Clicks on the card must not bubble to the stage backdrop (which would step back).
        card.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        _root = card;

        // Tunnel so Up/Down/Tab/Enter/Esc win before the TextBox consumes them.
        _root.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Rebuild();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => true;
    public bool DismissOnClickAway => true; // a click on the board steps back, like Esc

    // The board behind the card: the selected row's preview, or null ⇒ the stage's live map.
    public NavMap? BackdropBoard()
    {
        PaletteItem? sel = _selected >= 0 && _selected < _filtered.Count ? _filtered[_selected] : null;
        return sel?.Preview?.Invoke();
    }

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _chosen = false; // re-armed each time we're (re)shown — e.g. Esc back from a pushed sub-surface
        // Drop the stale filter when returning to this palette, so a pop-back lands on the full list. Setting
        // the text fires TextChanged ⇒ ApplyFilter, which rebuilds the rows and resets the selection.
        if (_clearSearchOnShow && !string.IsNullOrEmpty(_search.Text)) _search.Text = "";
        _search.Focus();
        // The initial selection's board was already painted by the stage when it set our content; the
        // backdrop is only re-rendered from here on as the selection moves (see Highlight).
    }

    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e) { } // handled by the tunneling handler on _root

    // ── Behaviour ──────────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _stage?.Back(); // return to the surface we opened over (or hide if we're the root)
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
            case Key.Delete:
                // Only hijack Delete as "remove this row" when it wouldn't edit the search text (no selection
                // and the caret already at the end, where Delete is a no-op) and the row offers a delete
                // action — so forward-delete still works while filtering, and rows with no OnDelete ignore it.
                if (SearchDeleteIsNoOp() && _filtered.Count > 0 && _filtered[_selected].OnDelete is not null)
                {
                    Delete(_filtered[_selected]);
                    e.Handled = true;
                }
                break;
        }
    }

    // True when pressing Delete in the search box would do nothing to the text (nothing selected and the
    // caret at the end), so it's free to repurpose as the row's delete shortcut.
    private bool SearchDeleteIsNoOp() =>
        _search.SelectionStart == _search.SelectionEnd && _search.CaretIndex >= (_search.Text?.Length ?? 0);

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
            _stage?.RefreshBackdrop();
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
        _stage?.RefreshBackdrop(); // repaint the board behind for the newly-selected row
    }

    private void Choose(PaletteItem item)
    {
        if (!item.Enabled) return; // greyed-out row: selectable so its reason can be read, but inert
        if (_chosen) return;
        _chosen = true;
        item.Choose();
        // If the action opened another surface (pushed content), it's now current — leave it. Otherwise the
        // action was terminal: unwind to where the chain started (the map, or dismiss).
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    // The row's secondary "delete" action (the Del shortcut). Same completion rule as Choose: if it pushed a
    // surface (a confirm card) that's now current, leave it; otherwise the action was terminal.
    private void Delete(PaletteItem item)
    {
        if (item.OnDelete is null || _chosen) return;
        _chosen = true;
        item.OnDelete();
        if (_stage?.Current == this) _stage.CompleteToBase();
    }
}
