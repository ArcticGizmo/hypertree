using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hypertree.Desktops;
using Hypertree.Status;

namespace Hypertree.App.Views;

/// <summary>
/// A floating "click to switch" panel, borrowed from Perch's integration strip and built into Hypertree
/// itself. It lists every row of the stack in map order — main and each branch — with, on the right, the
/// desktop a plain click would land on (the row's resume point). Clicking a row's name jumps to it;
/// clicking the desktop chip on a row with more than one desktop opens a small picker to choose which.
///
/// The header (logo + "Hypertree") is both the drag handle and the collapse toggle: click it to shrink to
/// a lone logo bubble, click the bubble (or press the toggle-switcher hotkey) to expand again. It's
/// draggable anywhere on screen and remembers where you left it.
///
/// Like <see cref="TaskbarLabel"/> it's pinned to every virtual desktop (survives navigation) and kept
/// topmost by a slow relift timer, and it's a tool window (no taskbar / alt-tab entry). Unlike the label
/// it must take clicks, so it is NOT click-through; it just avoids <i>stealing</i> focus on show
/// (<see cref="Window.ShowActivated"/> is false) — a click activates it briefly, which is invisible since
/// the jump switches desktop underneath it anyway.
/// </summary>
internal sealed class SwitcherWindow : Window
{
    private static readonly IBrush Ink = Palette.InkBrush;
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#B4C0D0"));
    private static readonly IBrush Muted = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush PanelBg = Palette.CardBgBrush;
    private static readonly IBrush PanelBorder = Palette.StrokeBrush;
    private static readonly IBrush Divider = Palette.StrokeBrush;
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private const int RelistMs = 1000;    // re-assert topmost this often (a surfacing window can bury us)
    private const int DragThresholdPx = 4; // press-and-move under this reads as a click, not a drag

    // What the panel renders and how a click acts on it — supplied by the app, which owns the model.
    private readonly Func<StatusSnapshot?> _provider;
    // (branchId, desktop) — branchId null means the main timeline; desktop null means the row's resume point.
    private readonly Action<Guid?, int?> _onJump;
    private readonly Action<bool, PixelPoint> _onMoved;  // persist a mode's position after a drag (collapsed?, at)
    private readonly Action<bool> _onCollapsedChanged;   // persist the collapse state
    private readonly Action _onExit;                     // right-click → close Hypertree entirely
    private readonly IDesktopController _desktops;

    private readonly DispatcherTimer _relift;
    private readonly Bitmap? _logo;

    private readonly Border _panel;   // the full list
    private readonly Border _bubble;  // the collapsed logo
    private readonly StackPanel _rows; // the branch rows inside _panel

    private bool _collapsed;
    private bool _enabled;
    private bool _suppressed;  // parked while the map overlay is up (it already shows the stack)
    private bool _pinned;

    // The dragged position for each state, kept apart so the panel and the bubble remember their own spot.
    // Null means "dock top-right"; a value means the user placed it there.
    private int? _expX, _expY, _colX, _colY;

    // Drag state (manual, in physical pixels via GetCursorPos, so it survives the window moving under us).
    private bool _pressed, _dragging;
    private PixelPoint _winStart;
    private POINT _curStart;

    public SwitcherWindow(Func<StatusSnapshot?> provider, Action<Guid?, int?> onJump,
                          Action<bool, PixelPoint> onMoved, Action<bool> onCollapsedChanged, Action onExit,
                          IDesktopController desktops, bool startCollapsed,
                          int? expandedX, int? expandedY, int? collapsedX, int? collapsedY)
    {
        _provider = provider;
        _onJump = onJump;
        _onMoved = onMoved;
        _onCollapsedChanged = onCollapsedChanged;
        _onExit = onExit;
        _desktops = desktops;
        _collapsed = startCollapsed;
        _expX = expandedX; _expY = expandedY;
        _colX = collapsedX; _colY = collapsedY;

        RequestedThemeVariant = ThemeVariant.Dark;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false; // appearing must never steal focus from the desktop; a click still activates it
        Topmost = true;
        IsVisible = false;

        try { _logo = DevChrome.AppLogo(); } catch { }

        _rows = new StackPanel { Spacing = 1 };
        _panel = BuildPanel();
        _bubble = BuildBubble();
        Content = _collapsed ? _bubble : _panel;

        _relift = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RelistMs) };
        _relift.Tick += (_, _) => Relift();

        // The content sizes to itself, so re-dock whenever that size changes (until the user drags it away).
        LayoutUpdated += (_, _) => Reposition();
    }

    // ── Show / hide plumbing (mirrors TaskbarLabel) ─────────────────────────────────

    /// <summary>Turn the switcher on. Shows it unless the overlay is currently suppressing it.</summary>
    public void Enable()
    {
        _enabled = true;
        if (!_suppressed) ShowPanel();
    }

    /// <summary>Hide the switcher and stop tracking (setting turned off).</summary>
    public void Disable()
    {
        _enabled = false;
        _relift.Stop();
        IsVisible = false;
    }

    /// <summary>Park (or restore) the switcher while the map overlay is up — the map already shows the
    /// stack, and parking removes the topmost-z fight with the overlay stage.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed) { _relift.Stop(); IsVisible = false; }
        else if (_enabled) ShowPanel();
    }

    private void ShowPanel()
    {
        Sync();
        ApplyModePosition(); // land on this mode's saved spot (or dock top-right)
        if (!IsVisible) Show(); // OnOpened pins + strips the taskbar entry the first time
        else EnsurePinned();
        if (!_relift.IsEnabled) _relift.Start();
    }

    // Whether the current mode (collapsed / expanded) has a saved position the user placed it at.
    private bool CurrentExplicit => _collapsed ? _colX is not null && _colY is not null
                                               : _expX is not null && _expY is not null;

    // Move the window to the current mode's saved position, or leave it for Reposition to dock top-right.
    private void ApplyModePosition()
    {
        if (_collapsed && _colX is int cx && _colY is int cy) Position = new PixelPoint(cx, cy);
        else if (!_collapsed && _expX is int ex && _expY is int ey) Position = new PixelPoint(ex, ey);
        else Reposition(); // no saved spot for this mode → auto-dock
    }

    // Record a drag into the current mode's slot (marks it explicit and keeps Reposition off its back).
    private void SetCurrentSaved(PixelPoint p)
    {
        if (_collapsed) { _colX = p.X; _colY = p.Y; }
        else { _expX = p.X; _expY = p.Y; }
    }

    /// <summary>Rebuild the branch rows from the current stack. Called by the app on every navigation
    /// (its own or an external one), so the list and the "you are here" marker never lag.</summary>
    public void Sync()
    {
        if (_collapsed) return; // nothing to show but the bubble
        _rows.Children.Clear();

        StatusSnapshot? status = _provider();
        if (status is null) return;

        for (int i = 0; i < status.Rows.Count; i++)
            _rows.Children.Add(BuildRow(status.Rows[i], i == status.Current.Row));
    }

    /// <summary>Flip between the full list and the collapsed logo bubble. The toggle-switcher hotkey and a
    /// click on the header/bubble both land here.</summary>
    public void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        Content = _collapsed ? _bubble : _panel;
        if (!_collapsed) Sync();
        ApplyModePosition(); // each state has its own saved spot
        _onCollapsedChanged(_collapsed);
    }

    // ── The full panel ──────────────────────────────────────────────────────────────

    private Border BuildPanel()
    {
        var header = BuildHeader();
        var body = new StackPanel
        {
            Children =
            {
                header,
                new Border { Height = 1, Background = Divider, Margin = new Thickness(0, 6) },
                _rows,
            },
        };
        return new Border
        {
            Background = PanelBg, BorderBrush = PanelBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8),
            MinWidth = 210,
            Child = body,
        };
    }

    // Logo + "Hypertree" caption on the left, a collapse chevron pinned to the right. The whole strip is
    // the drag handle and the collapse toggle; right-clicking it opens the context menu (Exit).
    private Control BuildHeader()
    {
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (_logo is not null)
            left.Children.Add(new Image { Source = _logo, Width = 18, Height = 18 });
        left.Children.Add(new TextBlock
        {
            Text = "Hypertree", Foreground = Ink, FontFamily = Mono, FontSize = 12,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });

        var chevron = new TextBlock
        {
            Text = "▾", Foreground = Muted, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(left);
        grid.Children.Add(chevron);

        var handle = new Border
        {
            Background = Brushes.Transparent, // transparent, but present, so the whole strip takes the press
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Padding = new Thickness(2, 1),
            Child = grid,
            ContextMenu = BuildContextMenu(),
        };
        WireDrag(handle);
        return handle;
    }

    // The right-click menu — currently just a hard exit, so the app is reachable straight from the switcher.
    private ContextMenu BuildContextMenu()
    {
        var exit = new MenuItem { Header = "Exit Hypertree", FontFamily = Mono, FontSize = 12 };
        exit.Click += (_, _) => _onExit();
        return new ContextMenu { Items = { exit } };
    }

    // ── The collapsed bubble ─────────────────────────────────────────────────────────

    private Border BuildBubble()
    {
        Control inner = _logo is not null
            ? new Image { Source = _logo, Width = 26, Height = 26 }
            : new TextBlock { Text = "H", Foreground = Ink, FontFamily = Mono, FontSize = 18, FontWeight = FontWeight.Bold };

        // No BoxShadow here: on a window sized exactly to the 44×44 circle, the drop shadow spilled into the
        // transparent rounded corners and read as a smeared gradient around the bubble. A solid fill plus the
        // border is clean.
        var bubble = new Border
        {
            Background = PanelBg, BorderBrush = PanelBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22), Width = 44, Height = 44,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ContextMenu = BuildContextMenu(),
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = inner,
            },
        };
        WireDrag(bubble);
        return bubble;
    }

    // ── A branch row ─────────────────────────────────────────────────────────────────

    // One line: an accent gutter (lit on the current row), the branch name (a click jumps to its resume
    // point), and the trailing desktop chip (its resume desktop; a dropdown when the row has more than one).
    private Control BuildRow(StatusRow row, bool current)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*,Auto"), Margin = new Thickness(0, 1) };

        var bar = new Border
        {
            Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2),
            Background = current ? Accent : Brushes.Transparent,
        };
        Grid.SetColumn(bar, 0);

        var name = new TextBlock
        {
            Text = row.Name, FontFamily = Mono, FontSize = 12,
            Foreground = current ? Ink : Dim, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0), MaxWidth = 150,
        };
        Grid.SetColumn(name, 1);

        Control chip = BuildDesktopChip(row);
        Grid.SetColumn(chip, 2);

        grid.Children.Add(bar);
        grid.Children.Add(name);
        grid.Children.Add(chip);

        // The whole line (minus the chip, which handles its own click) is a hover-highlighted jump target.
        var line = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(2, 0),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        line.PointerEntered += (_, _) => line.Background = RowHover;
        line.PointerExited += (_, _) => line.Background = Brushes.Transparent;
        line.PointerPressed += (_, e) =>
        {
            // The chip sits inside the line; let its own handler take a press on it.
            if (e.Source is Control s && IsWithin(s, chip)) return;
            if (e.GetCurrentPoint(line).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _onJump(BranchId(row), null); // null desktop → the row's resume point
            }
        };
        return line;
    }

    // The trailing desktop label. One desktop → a plain label (a click on the row already jumps there).
    // More than one → a button showing the resume desktop with a chevron, opening a picker of them all.
    private Control BuildDesktopChip(StatusRow row)
    {
        string resumeLabel = row.Cursor >= 0 && row.Cursor < row.Desktops.Count
            ? row.Desktops[row.Cursor].Label : "";

        if (row.Desktops.Count <= 1)
            return new TextBlock
            {
                Text = resumeLabel, FontFamily = Mono, FontSize = 11, Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 110, Margin = new Thickness(0, 0, 2, 0),
            };

        var text = new TextBlock
        {
            Text = resumeLabel, FontFamily = Mono, FontSize = 11, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 96,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 3,
                Children = { text, new TextBlock { Text = "▾", FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        chip.PointerEntered += (_, _) => { chip.Background = RowHover; text.Foreground = Ink; };
        chip.PointerExited += (_, _) => { chip.Background = Brushes.Transparent; text.Foreground = Muted; };
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true; // don't let the row's jump fire underneath
            ShowDesktopPicker(chip, row);
        };
        return chip;
    }

    // The small "which desktop" menu for a multi-desktop row: one item per desktop, the resume point ticked.
    private void ShowDesktopPicker(Control anchor, StatusRow row)
    {
        Guid? id = BranchId(row);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (int i = 0; i < row.Desktops.Count; i++)
        {
            int di = i; // capture per iteration
            var item = new MenuItem
            {
                Header = row.Desktops[i].Label,
                FontFamily = Mono, FontSize = 12,
                Icon = i == row.Cursor
                    ? new TextBlock { Text = "•", Foreground = Accent, FontSize = 14 }
                    : null,
            };
            item.Click += (_, _) => _onJump(id, di);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private static Guid? BranchId(StatusRow row) => row.IsMain ? null : row.Id;

    // ── Dragging (header / bubble) + click-to-toggle ────────────────────────────────

    private void WireDrag(Control handle)
    {
        handle.PointerPressed += OnHandlePressed;
        handle.PointerMoved += OnHandleMoved;
        handle.PointerReleased += OnHandleReleased;
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragging = false;
        GetCursorPos(out _curStart);
        _winStart = Position;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        if (!GetCursorPos(out POINT now)) return;
        int dx = now.X - _curStart.X, dy = now.Y - _curStart.Y;
        if (!_dragging && dx * dx + dy * dy < DragThresholdPx * DragThresholdPx) return;
        _dragging = true;
        Position = new PixelPoint(_winStart.X + dx, _winStart.Y + dy);
        SetCurrentSaved(Position); // once dragged, this mode owns its spot — stop auto-docking it
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        bool wasDrag = _dragging;
        _pressed = false;
        _dragging = false;
        e.Pointer.Capture(null);
        if (wasDrag) _onMoved(_collapsed, Position); // persist where this mode now sits
        else ToggleCollapsed();                      // a click on the header/bubble flips collapse
    }

    // Dock to the top-right of the primary screen while the user hasn't placed it themselves. Once dragged
    // (_hasExplicitPos), leave it exactly where they put it. Physical pixels throughout (Position is).
    private void Reposition()
    {
        if (!IsVisible || CurrentExplicit || Bounds.Width <= 0) return;
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double scale = screen.Scaling;
        int w = (int)(Bounds.Width * scale);
        int margin = (int)(16 * scale);
        int px = screen.WorkingArea.Right - w - margin;
        int py = screen.WorkingArea.Y + margin;
        var target = new PixelPoint(px, py);
        if (Position != target) Position = target;
    }

    // ── Pin / topmost (mirrors TaskbarLabel) ────────────────────────────────────────

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeToolWindow();
        EnsurePinned();
        Reposition();
    }

    private void EnsurePinned()
    {
        if (_pinned) return;
        nint h = TryGetPlatformHandle()?.Handle ?? 0;
        if (h == 0) return;
        try { _desktops.PinWindow(h); _pinned = true; } catch { /* best-effort — losing the pin isn't fatal */ }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private void Relift() => WindowFx.LiftTopmost(TryGetPlatformHandle()?.Handle ?? 0);

    // Keep it out of the taskbar / alt-tab (TOOLWINDOW only). Deliberately NOT click-through / no-activate:
    // the switcher must take clicks (WindowFx.SetToolWindow, unlike SetClickThrough, adds neither). Focus
    // isn't stolen on *show* either way (ShowActivated = false); a click briefly activates it, which the
    // desktop switch hides.
    private void MakeToolWindow() => WindowFx.SetToolWindow(TryGetPlatformHandle()?.Handle ?? 0);

    private static bool IsWithin(Control node, Control ancestor)
    {
        for (Visual? v = node; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, ancestor)) return true;
        return false;
    }
}
