using System.Runtime.InteropServices;
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
    /// <summary>Delete a desktop (× badge) — top-row index, or group index + desktop index.</summary>
    public event Action<int>? DeleteTopRequested;
    public event Action<int, int>? DeleteGroupDesktopRequested;
    /// <summary>Footer actions.</summary>
    public event Action? NewGroupRequested;
    public event Action<int>? RemoveGroupRequested;
    public event Action? DeleteCurrentRequested;
    /// <summary>The cog icon — open settings.</summary>
    public event Action? SettingsRequested;

    public MapOverlay(IDesktopController desktops) => _desktops = desktops;

    public void Open(NavMap map)
    {
        if (IsOpen) return;

        _map = new MapWindow();
        _map.CloseRequested += Close;
        _map.GoToTopRequested += i => GoToTopRequested?.Invoke(i);
        _map.GoToGroupRequested += (g, d) => GoToGroupRequested?.Invoke(g, d);
        _map.DeleteTopRequested += i => DeleteTopRequested?.Invoke(i);
        _map.DeleteGroupDesktopRequested += (g, d) => DeleteGroupDesktopRequested?.Invoke(g, d);
        _map.NewGroupRequested += () => NewGroupRequested?.Invoke();
        _map.RemoveGroupRequested += g => RemoveGroupRequested?.Invoke(g);
        _map.DeleteCurrentRequested += () => DeleteCurrentRequested?.Invoke();
        _map.SettingsRequested += () => SettingsRequested?.Invoke();
        _map.Show();          // realize the handle so Screens is available, then size + fill it
        _map.Render(map);

        // Dim every OTHER monitor; the map window itself covers the primary and carries its own dim,
        // so the primary isn't double-dimmed.
        foreach (Screen s in _map.Screens.All)
        {
            if (_map.Screens.Primary is { } p && s.Bounds == p.Bounds) continue;
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

    public void Refresh(NavMap map)
    {
        _map?.Render(map);
        // Navigating switches the desktop, which can surface the target desktop's foreground window
        // (e.g. VS Code) above the pinned map. Re-lift the map to the top of the topmost band after
        // each interaction — without activating, so we don't steal focus or disturb the hotkey thread.
        BringToTop();
    }

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // Lift the map (and, beneath it, the dim windows) back to the top of the always-on-top band.
    private void BringToTop()
    {
        foreach (Window d in _dims) Lift(d);
        if (_map is not null) Lift(_map); // map last, so it sits above the dims
    }

    private static void Lift(Window w)
    {
        nint h = w.TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

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

/// <summary>The interactive map: a full-screen, box-less surface on the primary monitor (F1/F3). Dim
/// backdrop, the centred board drawn directly on it (clickable tiles), a hint at the top and the
/// add/remove/delete actions reflowed to the bottom of the screen — no framing card.</summary>
internal sealed class MapWindow : Window
{
    public event Action? CloseRequested;
    public event Action<int>? GoToTopRequested;
    public event Action<int, int>? GoToGroupRequested;
    public event Action<int>? DeleteTopRequested;
    public event Action<int, int>? DeleteGroupDesktopRequested;
    public event Action? NewGroupRequested;
    public event Action<int>? RemoveGroupRequested;
    public event Action? DeleteCurrentRequested;
    public event Action? SettingsRequested;

    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));

    public MapWindow()
    {
        Title = "Hypertree";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12)); // the dim backdrop
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) CloseRequested?.Invoke();
    }

    public void Render(NavMap map)
    {
        CoverPrimary();

        Control board = BoardView.Render(map, Width, Height, 1.0,
            onTopClick: i => GoToTopRequested?.Invoke(i),
            onGroupClick: (g, d) => GoToGroupRequested?.Invoke(g, d),
            onTopDelete: i => DeleteTopRequested?.Invoke(i),
            onGroupDelete: (g, d) => DeleteGroupDesktopRequested?.Invoke(g, d));

        var hint = new TextBlock
        {
            Text = "click a desktop to jump · Esc to close", FontSize = 12, Foreground = FgDim,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
        };

        // Cog in the top-right corner — open settings.
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

        // Delete the currently-selected desktop (also available per-tile via the × badge).
        buttons.Children.Add(ActionButton("Delete desktop", () => DeleteCurrentRequested?.Invoke()));

        // Remove targets the first group in the stack, if any.
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

        Content = new Grid { Children = { board, hint, footer, cog } };
    }

    private static readonly Color BtnBg = Color.Parse("#2A3444"), BtnBgHover = Color.Parse("#37455B"), BtnBorder = Color.Parse("#3C4A5E");

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

    // Fill the primary monitor (DIPs), matching the flash so both modes present identically.
    private void CoverPrimary()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }
}
