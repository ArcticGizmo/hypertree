using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Platform;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>One filterable row in a <see cref="PaletteWindow"/>: a primary <paramref name="Label"/>,
/// an optional dimmer <paramref name="Detail"/> (also folded into the match text, so e.g. a group
/// name filters its desktops), an optional trailing <paramref name="Glyph"/>, the action to run when
/// it's chosen, and — in preview mode — a <paramref name="Preview"/> board to show while it's the
/// selected row (so you can see where a jump will land).</summary>
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
/// The shared spotlight/command-palette base (F4 &amp; F5), modelled on perch's SessionSwitcherWindow:
/// a keyboard-driven palette summoned by a global hotkey. A search box takes focus immediately; type
/// to filter (case-insensitive Contains over label + detail), Up/Down or Tab to move, Enter to choose
/// the highlighted row, Esc or click-away to dismiss. Because a tray-hotkey window must steal focus
/// from a background process, it force-foregrounds on open via <see cref="IForegroundActivator"/>.
///
/// Two layouts: the default centred card (command palette), and <b>preview mode</b> (the jump palette)
/// — a full-screen dim surface with the search card anchored to the top and the board rendered in the
/// middle, re-drawn to highlight the currently-selected desktop so you can see the destination.
/// </summary>
internal sealed class PaletteWindow : Window
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

    private readonly IForegroundActivator _activator;
    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly Func<string, PaletteItem?>? _createRow;
    private readonly bool _previewMode;

    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly List<Control> _rows = new();
    private readonly Border? _previewBorder; // holds the board in preview mode
    private List<PaletteItem> _filtered;
    private int _selected;
    private bool _chosen;
    private bool _ready; // armed once focus settles, so the foreground-forcing dance can't self-dismiss

    public PaletteWindow(string placeholder, string footerHint, IReadOnlyList<PaletteItem> items,
                         IForegroundActivator activator, Func<string, PaletteItem?>? createRow = null,
                         bool previewMode = false)
    {
        _activator = activator;
        _all = items;
        _createRow = createRow;
        _previewMode = previewMode;
        _filtered = items.ToList();

        WindowDecorations = WindowDecorations.None;
        RequestedThemeVariant = ThemeVariant.Dark; // else the themed search box renders light (app is FluentTheme light)
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;

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

        if (previewMode)
        {
            // Full-screen dim surface: search card anchored top-centre, board filling the space below.
            Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12));
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeToContent = SizeToContent.Manual;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(0, 44, 0, 0);

            _previewBorder = new Border { Margin = new Thickness(0, 0, 0, 24) };
            // Re-render the board whenever its region is (re)sized — including the first real layout.
            _previewBorder.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdatePreview(); };

            // DockPanel: card takes its natural height at the top, the board fills the space beneath it.
            DockPanel.SetDock(card, Dock.Top);
            Content = new DockPanel { Children = { card, _previewBorder } };
        }
        else
        {
            Background = Brushes.Transparent;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = card;
        }

        // Tunnel so Up/Down/Tab/Enter/Esc win before the TextBox consumes them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => { if (_previewMode) CoverPrimary(); _search.Focus(); };
        Deactivated += (_, _) => { if (_ready && !_chosen) Close(); };

        Rebuild();
    }

    /// <summary>Force to the foreground and hand the search box focus — the app calls this right after
    /// <see cref="Window.Show()"/>, since a global hotkey in a background tray doesn't grant foreground
    /// rights on its own.</summary>
    public void TakeFocus()
    {
        if (TryGetPlatformHandle() is { } handle) _activator.ForceForeground(handle.Handle);
        Activate();
        _search.Focus();
        // Arm dismiss-on-deactivate only after this settles, so the foreground dance above doesn't
        // immediately self-close the window.
        Dispatcher.UIThread.Post(() => _ready = true, DispatcherPriority.Background);
    }

    private void CoverPrimary()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
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

        // Always offer a create row when the query is non-empty and no item matches it exactly (F4).
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
        Close();
    }
}
