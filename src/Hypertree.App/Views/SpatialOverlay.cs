using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.App.Views.Scene;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The interactive <b>spatial map</b> — the second map model, presented on the same <see cref="OverlayStage"/>
/// as the row <see cref="MapOverlay"/>. Desktops are freely-placed rooms; a blue selection cursor moves over
/// a stationary map (arrow keys pick the nearest room in that direction, or click a room), <c>Enter</c> /
/// double-click switches, and <c>Tab</c> flips back to the row model. It shares the app's dead-zone
/// <see cref="MapCamera"/> with the flash and the row map, so switching models never teleports the view.
///
/// M2 covers viewing, navigation, jump and the model swap. Placement, groups, delete and tidy — the edits —
/// arrive in later milestones; the overlay is built to grow those in without touching the row map.
/// </summary>
internal sealed class SpatialOverlay : IStageContent
{
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush FgDim = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly Color LegendBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22);
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A);
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly OverlayStage _stage;
    private readonly MapCamera _camera;
    private readonly Grid _root = new();

    private SpatialSource _source = new(Array.Empty<SpatialGroupSource>());
    private SpatialState _state = new();
    private DesktopId? _cursor;   // the blue selection; null until homed onto the current desktop
    private bool _initialised;

    /// <summary>Switch to a room (Enter / double-click) — App resolves the id to a jump.</summary>
    public event Action<DesktopId>? JumpRoomRequested;
    /// <summary>Tab — swap back to the row map. App flips the persisted model and re-opens.</summary>
    public event Action? SwapModelRequested;

    public SpatialOverlay(OverlayStage stage, MapCamera camera)
    {
        _stage = stage;
        _camera = camera;
    }

    public bool IsOpen => _stage.Current == this;

    /// <summary>Open the spatial map, homing the selection onto the desktop you're on.</summary>
    public void Open(SpatialSource source, SpatialState state)
    {
        _source = source;
        _state = state;
        _initialised = false;
        _stage.Summon(this);
    }

    /// <summary>Stash a fresh scene, preserving the cursor. Redraws now if current.</summary>
    public void SetSource(SpatialSource source, SpatialState state)
    {
        _source = source;
        _state = state;
        if (IsOpen) Render();
    }

    /// <summary>Redraw and re-home the selection onto the desktop you're now on — after a real switch.</summary>
    public void SyncToCurrent(SpatialSource source, SpatialState state)
    {
        if (!IsOpen) return;
        _source = source;
        _state = state;
        _initialised = false;
        Render();
    }

    public void Close()
    {
        if (IsOpen) _stage.Back();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface;
    public bool Durable => true;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage) => Render();
    public void OnRemoved() => _initialised = false;

    public void OnKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Tab: SwapModelRequested?.Invoke(); e.Handled = true; break;
            case Key.Enter: if (_cursor is { } c) JumpRoomRequested?.Invoke(c); e.Handled = true; break;
            case Key.Left: Nudge(-1, 0); e.Handled = true; break;
            case Key.Right: Nudge(1, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -1); e.Handled = true; break;
            case Key.Down: Nudge(0, 1); e.Handled = true; break;
        }
    }

    // ── Selection ──────────────────────────────────────────────────────────────

    private SpatialScene Scene() => SpatialScene.From(_source, _state);

    // Home the cursor onto the desktop the user is actually on (the source's selected room), else the first.
    private void InitCursor()
    {
        var scene = Scene();
        _cursor = scene.Rooms.FirstOrDefault(r => r.Selected)?.Id
               ?? (scene.Rooms.Count > 0 ? scene.Rooms[0].Id : (DesktopId?)null);
    }

    // Arrow-select: step to the nearest room in the pressed direction, favouring the axis of travel — the
    // 2-D analog of the row map's ←/→ along a row and ↑/↓ between rows.
    private void Nudge(int dx, int dy)
    {
        var rooms = Scene().Rooms;
        if (rooms.Count == 0) return;
        DesktopId curId = _cursor ?? rooms.First().Id;
        SpatialRoom? cur = rooms.FirstOrDefault(r => r.Id == curId);
        if (cur is null) { _cursor = rooms[0].Id; Render(); return; }

        SpatialRoom? best = null;
        int bestScore = int.MaxValue;
        foreach (SpatialRoom r in rooms)
        {
            if (r.Id == curId) continue;
            int ox = r.Pos.X - cur.Pos.X, oy = r.Pos.Y - cur.Pos.Y;
            if (dx != 0 && Math.Sign(ox) != dx) continue;
            if (dy != 0 && Math.Sign(oy) != dy) continue;
            if (dx != 0 && Math.Abs(oy) > Math.Abs(ox)) continue; // keep to the travel axis
            if (dy != 0 && Math.Abs(ox) > Math.Abs(oy)) continue;
            int d = Math.Abs(ox) + Math.Abs(oy);
            if (d < bestScore) { bestScore = d; best = r; }
        }
        if (best is not null) { _cursor = best.Id; Render(); }
    }

    // ── Render ───────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!_initialised) { InitCursor(); _initialised = true; }

        double width = _stage.HostWidth > 0 ? _stage.HostWidth : 1280;
        double height = _stage.HostHeight > 0 ? _stage.HostHeight : 800;

        SpatialScene display = _cursor is { } c
            ? SpatialScene.From(_source, _state, c)
            : SpatialScene.From(_source, _state);

        Control board = SpatialPainter.Render(display, width, height, 1.0, _camera,
            onClick: id => { _cursor = id; Render(); },
            onActivate: id => JumpRoomRequested?.Invoke(id));

        _root.Children.Clear();
        _root.Children.Add(board);
        _root.Children.Add(BuildLegend());

        _stage.BringToFront();
    }

    private Control BuildLegend()
    {
        var rows = new StackPanel { Spacing = 7 };
        rows.Children.Add(new TextBlock
        {
            Text = "Spatial map", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        });
        rows.Children.Add(LegendRow("←→↑↓", "select the nearest room"));
        rows.Children.Add(LegendRow("Enter", "switch to selected"));
        rows.Children.Add(LegendRow("Ctrl+Alt+←→↑↓", "switch to a desktop"));
        rows.Children.Add(LegendRow("Tab", "back to the list view"));
        rows.Children.Add(LegendRow("Esc", "close"));
        rows.Children.Add(new TextBlock
        {
            Text = "click to select · double-click to switch", FontSize = 11, Foreground = FgDim,
            Margin = new Avalonia.Thickness(0, 5, 0, 0),
        });

        var legend = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new Avalonia.CornerRadius(12), Padding = new Avalonia.Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(24, 24, 0, 0), Child = rows,
        };
        legend.PointerPressed += (_, e) => e.Handled = true; // reading the legend never selects behind it
        return legend;
    }

    private static Control LegendRow(string key, string desc)
    {
        var cap = new Border
        {
            Background = new SolidColorBrush(KeyCapBg),
            CornerRadius = new Avalonia.CornerRadius(5), Padding = new Avalonia.Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent, FontFamily = Mono,
            },
        };
        Grid.SetColumn(cap, 0);
        var label = new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        grid.Children.Add(cap);
        grid.Children.Add(label);
        return grid;
    }
}
