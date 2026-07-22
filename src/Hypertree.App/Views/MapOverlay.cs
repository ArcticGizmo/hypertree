using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Hypertree.Desktops;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive map/command surface, opened on a dedicated hotkey. Dims every screen with a grey
/// backdrop to pull focus, then shows the full board on the primary monitor. Tiles are clickable
/// (jump to that desktop). All overlay windows are PINNED to every desktop so they stay visible while
/// you navigate underneath — the map only closes on Esc, a backdrop click, or toggling the hotkey.
/// </summary>
internal sealed class MapOverlay
{
    private readonly IDesktopController _desktops;
    private readonly List<Window> _dims = new();
    private MapWindow? _map;

    public bool IsOpen => _map is not null;

    /// <summary>Click a top-row desktop (index) to jump there.</summary>
    public event Action<int>? GoToTopRequested;
    /// <summary>Click a group desktop (group index, desktop index) to jump there.</summary>
    public event Action<int, int>? GoToGroupRequested;
    /// <summary>Footer actions.</summary>
    public event Action? NewGroupRequested;
    public event Action<int>? RemoveGroupRequested;

    public MapOverlay(IDesktopController desktops) => _desktops = desktops;

    public void Open(NavMap map)
    {
        if (IsOpen) return;

        _map = new MapWindow();
        _map.CloseRequested += Close;
        _map.GoToTopRequested += i => GoToTopRequested?.Invoke(i);
        _map.GoToGroupRequested += (g, d) => GoToGroupRequested?.Invoke(g, d);
        _map.NewGroupRequested += () => NewGroupRequested?.Invoke();
        _map.RemoveGroupRequested += g => RemoveGroupRequested?.Invoke(g);
        _map.Render(map);
        _map.Show();

        foreach (Screen s in _map.Screens.All)
        {
            Window dim = MakeDim(s);
            dim.Show();
            _dims.Add(dim);
        }

        _map.Topmost = false;
        _map.Topmost = true;
        _map.Activate();

        // Pin every overlay window so the desktop switch (from navigating) doesn't hide them.
        Pin(_map);
        foreach (Window d in _dims) Pin(d);
    }

    public void Refresh(NavMap map) => _map?.Render(map);

    public void Close()
    {
        foreach (Window d in _dims) d.Close();
        _dims.Clear();
        _map?.Close();
        _map = null;
    }

    private void Pin(Window w)
    {
        nint h = w.TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0)
        {
            try { _desktops.PinWindow(h); } catch { /* best-effort */ }
        }
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
        dim.PointerPressed += (_, _) => Close();
        return dim;
    }
}

/// <summary>The primary-monitor card: the board (clickable) plus a footer to add/remove groups.</summary>
internal sealed class MapWindow : Window
{
    public event Action? CloseRequested;
    public event Action<int>? GoToTopRequested;
    public event Action<int, int>? GoToGroupRequested;
    public event Action? NewGroupRequested;
    public event Action<int>? RemoveGroupRequested;

    private readonly Border _card;
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#69748A"));

    public MapWindow()
    {
        Title = "Hypertree";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x19, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22, 18),
        };
        Content = _card;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) CloseRequested?.Invoke();
    }

    public void Render(NavMap map)
    {
        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "Streams", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Fg,
            [DockPanel.DockProperty] = Dock.Left,
        });
        header.Children.Add(new TextBlock
        {
            Text = "click a desktop to jump · Esc to close", FontSize = 12, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
        });

        Control board = BoardView.Render(map, 1.0,
            onTopClick: i => GoToTopRequested?.Invoke(i),
            onGroupClick: (g, d) => GoToGroupRequested?.Invoke(g, d));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var add = new Button { Content = "+ New group", FontSize = 12 };
        add.Click += (_, _) => NewGroupRequested?.Invoke();
        footer.Children.Add(add);
        // Remove targets the active (nearest) group, if any.
        if (map.Groups.Count > 0)
        {
            int activeIndex = map.Groups[0].Index;
            var remove = new Button { Content = $"Remove “{map.Groups[0].Name}”", FontSize = 12 };
            remove.Click += (_, _) => RemoveGroupRequested?.Invoke(activeIndex);
            footer.Children.Add(remove);
        }

        _card.Child = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 14, Children = { header, board, footer },
        };
    }
}
