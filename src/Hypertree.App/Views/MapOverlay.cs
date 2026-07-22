using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive map/command surface, opened on a dedicated hotkey. Dims every screen with a grey
/// backdrop to pull focus, then shows the full Model-P board on the primary monitor (via
/// <see cref="BoardView"/>) with a footer for configuring the current stream. The place to eyeball
/// the whole structure and add/remove scopes while debugging. Click a backdrop or press Esc to close.
/// </summary>
internal sealed class MapOverlay
{
    private readonly List<Window> _dims = new();
    private MapWindow? _map;

    public bool IsOpen => _map is not null;

    /// <summary>Requested creation/removal of a scope on the anchor with this index.</summary>
    public event Action<int>? AddScopeRequested;
    public event Action<int>? RemoveScopeRequested;

    public void Open(NavMap map)
    {
        if (IsOpen) return;

        _map = new MapWindow();
        _map.CloseRequested += Close;
        _map.AddScopeRequested += i => AddScopeRequested?.Invoke(i);
        _map.RemoveScopeRequested += i => RemoveScopeRequested?.Invoke(i);
        _map.Render(map);
        _map.Show();   // realizes the handle so Screens is populated

        foreach (Screen s in _map.Screens.All)
        {
            Window dim = MakeDim(s);
            dim.Show();
            _dims.Add(dim);
        }

        // Re-raise the map above the just-shown backdrops and give it focus for Esc.
        _map.Topmost = false;
        _map.Topmost = true;
        _map.Activate();
    }

    public void Refresh(NavMap map) => _map?.Render(map);

    public void Close()
    {
        foreach (Window d in _dims) d.Close();
        _dims.Clear();
        _map?.Close();
        _map = null;
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
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Position = s.Bounds.Position,
            Width = s.Bounds.Width / scale,
            Height = s.Bounds.Height / scale,
        };
        dim.PointerPressed += (_, _) => Close();
        return dim;
    }
}

/// <summary>The primary-monitor card: the Model-P board plus a footer to configure the current stream.</summary>
internal sealed class MapWindow : Window
{
    public event Action? CloseRequested;
    public event Action<int>? AddScopeRequested;
    public event Action<int>? RemoveScopeRequested;

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
        int cur = 0;
        for (int i = 0; i < map.Anchors.Count; i++) if (map.Anchors[i].IsCurrentColumn) cur = i;
        bool hasScope = map.ScopeDesktops is not null;
        string anchorLabel = map.Anchors[cur].Label;

        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "Streams", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Fg,
            [DockPanel.DockProperty] = Dock.Left,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Esc to close", FontSize = 12, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
        });

        Control board = BoardView.Render(map, 1.0);

        // Footer: configure the current stream. (Navigate to another column, then reopen, to
        // configure it — arrows close the overlay and move you there.)
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(new TextBlock
        {
            Text = $"“{anchorLabel}”", Foreground = FgDim, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (hasScope)
        {
            var remove = new Button { Content = "Remove scope", FontSize = 12 };
            remove.Click += (_, _) => RemoveScopeRequested?.Invoke(cur);
            footer.Children.Add(remove);
        }
        else
        {
            var add = new Button { Content = "+ Add scope", FontSize = 12 };
            add.Click += (_, _) => AddScopeRequested?.Invoke(cur);
            footer.Children.Add(add);
        }

        _card.Child = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 14, Children = { header, board, footer },
        };
    }
}
