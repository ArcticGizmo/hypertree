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
/// backdrop to pull focus, then shows the full stream tree on the primary monitor with add/remove
/// controls — the place to configure scopes and to eyeball the whole structure while debugging.
/// Click a backdrop or press Esc to close. Unlike the transient <see cref="HudWindow"/> flash, this
/// is focusable and captures input.
/// </summary>
internal sealed class MapOverlay
{
    private readonly List<Window> _dims = new();
    private MapWindow? _map;

    public bool IsOpen => _map is not null;

    /// <summary>Requested creation/removal of a scope on the anchor with this index.</summary>
    public event Action<int>? AddScopeRequested;
    public event Action<int>? RemoveScopeRequested;

    public void Open(IReadOnlyList<StreamInfo> streams)
    {
        if (IsOpen) return;

        _map = new MapWindow();
        _map.CloseRequested += Close;
        _map.AddScopeRequested += i => AddScopeRequested?.Invoke(i);
        _map.RemoveScopeRequested += i => RemoveScopeRequested?.Invoke(i);
        _map.Render(streams);
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

    public void Refresh(IReadOnlyList<StreamInfo> streams) => _map?.Render(streams);

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

/// <summary>The primary-monitor card: the stream list with per-anchor add/remove controls.</summary>
internal sealed class MapWindow : Window
{
    public event Action? CloseRequested;
    public event Action<int>? AddScopeRequested;
    public event Action<int>? RemoveScopeRequested;

    private readonly StackPanel _list;

    private static readonly IBrush Current = new SolidColorBrush(Color.Parse("#2D7D46"));
    private static readonly IBrush PillBg  = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush Accent  = new SolidColorBrush(Color.Parse("#6FD08C"));
    private static readonly IBrush Fg      = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush FgDim   = new SolidColorBrush(Color.Parse("#9A9A9A"));

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

        _list = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8 };

        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "Streams", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Fg,
            [DockPanel.DockProperty] = Dock.Left,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Esc to close", FontSize = 12, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
        });

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22, 18),
            MinWidth = 380,
            Child = new StackPanel { Orientation = Orientation.Vertical, Spacing = 14, Children = { header, _list } },
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) CloseRequested?.Invoke();
    }

    public void Render(IReadOnlyList<StreamInfo> streams)
    {
        _list.Children.Clear();
        foreach (StreamInfo s in streams)
            _list.Children.Add(BuildRow(s));
    }

    private Control BuildRow(StreamInfo s)
    {
        var row = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        // Anchor line: name + a marker if it's the current column, then the config button.
        var top = new DockPanel { LastChildFill = false };
        var name = new TextBlock
        {
            Text = (s.IsCurrentColumn ? "● " : "") + s.AnchorLabel,
            Foreground = s.IsCurrentColumn ? Accent : Fg,
            FontSize = 15, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Left,
        };
        top.Children.Add(name);

        if (s.ScopeName is null)
        {
            var add = new Button { Content = "+ Add scope", FontSize = 12, [DockPanel.DockProperty] = Dock.Right };
            add.Click += (_, _) => AddScopeRequested?.Invoke(s.Index);
            top.Children.Add(add);
        }
        else
        {
            var remove = new Button { Content = "Remove", FontSize = 12, [DockPanel.DockProperty] = Dock.Right };
            remove.Click += (_, _) => RemoveScopeRequested?.Invoke(s.Index);
            top.Children.Add(remove);
        }
        row.Children.Add(top);

        // Scope line: name + desktop pills, indented to read as "hangs beneath".
        if (s.ScopeName is not null)
        {
            var scopeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(16, 0, 0, 0),
            };
            scopeRow.Children.Add(new TextBlock
            {
                Text = "▸ " + s.ScopeName, Foreground = FgDim, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            });
            foreach (string label in s.ScopeDesktops)
                scopeRow.Children.Add(new Border
                {
                    Background = PillBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 3),
                    Child = new TextBlock { Text = label, Foreground = Fg, FontSize = 12 },
                });
            row.Children.Add(scopeRow);
        }

        return new Border
        {
            Background = s.IsCurrentColumn ? new SolidColorBrush(Color.FromArgb(0x22, 0x6F, 0xD0, 0x8C)) : Brushes.Transparent,
            BorderBrush = s.IsCurrentColumn ? Current : new SolidColorBrush(Color.Parse("#333")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Child = row,
        };
    }
}
