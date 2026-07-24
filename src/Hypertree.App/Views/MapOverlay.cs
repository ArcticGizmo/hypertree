using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive map/command surface, presented on the shared <see cref="OverlayStage"/>. Renders the
/// full board on the primary monitor over the stage's dim backdrop; tiles are clickable (jump to that
/// desktop). Because it lives on the persistent stage, opening it from the command palette is an
/// in-place content swap (no flash), and it survives the desktop switches of navigation (the stage is
/// pinned to every desktop). Closes on Esc, a backdrop click on another monitor, or toggling the hotkey.
/// </summary>
internal sealed class MapOverlay : IStageContent
{
    private readonly OverlayStage _stage;

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly Color BtnBg = Color.Parse("#2A3444"), BtnBgHover = Color.Parse("#37455B"), BtnBorder = Color.Parse("#3C4A5E");

    private Control _view = new Panel();
    private Border? _boardLayer;
    private NavMap _map = new(Array.Empty<NavMapTile>(), 0, true, Array.Empty<NavMapGroup>());

    /// <summary>Click a top-row desktop (index) to jump there.</summary>
    public event Action<int>? GoToTopRequested;
    /// <summary>Click a group desktop (group index, desktop index) to jump there.</summary>
    public event Action<int, int>? GoToGroupRequested;
    /// <summary>Delete a desktop (× badge) — top-row index, or group index + desktop index.</summary>
    public event Action<int>? DeleteTopRequested;
    public event Action<int, int>? DeleteGroupDesktopRequested;
    /// <summary>Footer actions.</summary>
    public event Action? NewGroupRequested;
    public event Action<int>? RemoveGroupRequested;
    public event Action? DeleteCurrentRequested;
    /// <summary>The cog icon — open settings.</summary>
    public event Action? SettingsRequested;

    public MapOverlay(OverlayStage stage) => _stage = stage;

    public bool IsOpen => _stage.Current == this;

    public void Open(NavMap map)
    {
        _map = map;
        _view = BuildView(map);
        _stage.Present(this);
    }

    /// <summary>Redraw after a navigation. Re-hosts the rebuilt board on the stage and re-lifts it.</summary>
    public void Refresh(NavMap map)
    {
        if (!IsOpen) return;
        _map = map;
        _view = BuildView(map);
        _stage.Update(this);
    }

    public void Close()
    {
        if (IsOpen) _stage.Dismiss();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _view;
    public bool Dim => true;
    public bool DismissOnDeactivate => false; // must survive the deactivation a desktop switch causes
    public bool DismissOnClickAway => false;  // clicking the primary board never closes; Esc / dim click do

    public void OnPresented(OverlayStage stage) { }
    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    // ── View ───────────────────────────────────────────────────────────────────

    // The board fills the surface; because its size isn't known until the stage lays the view out, it's
    // (re)rendered whenever the board layer is resized. The footer / hint / cog overlay on top.
    private Control BuildView(NavMap map)
    {
        _boardLayer = new Border();
        _boardLayer.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) RenderBoard(); };

        var hint = new TextBlock
        {
            Text = "click a desktop to jump · Esc to close", FontSize = 12, Foreground = FgDim,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
        };

        var cog = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(BtnBg), BorderBrush = new SolidColorBrush(BtnBorder),
            BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 24, 0),
            Child = new TextBlock
            {
                Text = "⚙", FontSize = 17, Foreground = Fg,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        cog.PointerEntered += (_, _) => cog.Background = new SolidColorBrush(BtnBgHover);
        cog.PointerExited += (_, _) => cog.Background = new SolidColorBrush(BtnBg);
        cog.PointerPressed += (_, e) => { e.Handled = true; SettingsRequested?.Invoke(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(ActionButton("+ New group", () => NewGroupRequested?.Invoke()));
        buttons.Children.Add(ActionButton("Delete desktop", () => DeleteCurrentRequested?.Invoke()));
        if (map.Groups.Count > 0)
        {
            int firstIndex = map.Groups[0].Index;
            buttons.Children.Add(ActionButton($"Remove “{map.Groups[0].Name}”", () => RemoveGroupRequested?.Invoke(firstIndex)));
        }

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x14, 0x19, 0x22)),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 28), Child = buttons,
        };

        return new Grid { Children = { _boardLayer, hint, footer, cog } };
    }

    private void RenderBoard()
    {
        if (_boardLayer is null) return;
        Size sz = _boardLayer.Bounds.Size;
        if (sz.Width < 10 || sz.Height < 10) return;
        _boardLayer.Child = BoardView.Render(_map, sz.Width, sz.Height, 1.0,
            onTopClick: i => GoToTopRequested?.Invoke(i),
            onGroupClick: (g, d) => GoToGroupRequested?.Invoke(g, d),
            onTopDelete: i => DeleteTopRequested?.Invoke(i),
            onGroupDelete: (g, d) => DeleteGroupDesktopRequested?.Invoke(g, d));
    }

    // A clearly-visible action control for the footer. The default themed Button renders dark-on-dark
    // against the dim backdrop, so — like the board — we draw our own with an explicit hover state.
    private static Control ActionButton(string text, Action onClick)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(BtnBg),
            BorderBrush = new SolidColorBrush(BtnBorder), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text, FontSize = 12, Foreground = Fg,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        border.PointerEntered += (_, _) => border.Background = new SolidColorBrush(BtnBgHover);
        border.PointerExited += (_, _) => border.Background = new SolidColorBrush(BtnBg);
        border.PointerPressed += (_, e) => { e.Handled = true; onClick(); };
        return border;
    }
}
