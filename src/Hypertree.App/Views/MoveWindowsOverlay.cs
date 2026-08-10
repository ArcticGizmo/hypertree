using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.App.Views.Scene;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// The two-phase "move windows" flow. Phase 1 is the shared <see cref="WindowPickerContent"/> grid — the
/// current desktop's windows, multi-select, with a search box. Phase 2 (this class's own) shows the
/// <b>spatial map</b> and lets the user drive a blue selection cursor to a destination room (arrow keys pick
/// the nearest room in that direction, sharing the map's own resolver), then drops the selected windows onto
/// it with Enter.
///
/// Unlike the old row-map phase, navigating never switches desktops: the origin stays current and the cursor
/// just roams the stationary map, so cancelling has nothing to undo. It holds no model — the destination and
/// the drop are raised as events for <c>App</c> (which owns the <see cref="NavigationModel"/> and desktop
/// controller); the scene is pulled live via <see cref="SceneProvider"/>.
/// </summary>
internal sealed class MoveContent : WindowPickerContent
{
    private bool _targeting;
    private DesktopId? _cursor; // the blue selection over the map; homed onto the origin on entry

    /// <summary>Supplies the live spatial scene for phase 2 (App: the current source + persisted state).</summary>
    public Func<(SpatialSource Source, SpatialState State)>? SceneProvider;
    /// <summary>Phase-2 Enter — App moves these windows onto the chosen destination room.</summary>
    public event Action<DesktopId, IReadOnlyList<nint>>? MoveRequested;

    public MoveContent(WindowMoveSession session, double initialZoom = 1.0) : base(session, initialZoom) { }

    protected override string PickerHint => "←→↑↓ move · Space tick · Enter choose destination · Esc cancel";
    protected override string EmptyHint => "No windows to move on this desktop · Esc to close";

    // Nothing to restore on a cancel — phase 2 never leaves the origin — so teardown is just the base's
    // thumbnail disposal.
    public override void OnRemoved() => base.OnRemoved();

    // Phase 1 Enter (with a selection) advances to picking a destination on the map.
    protected override void ConfirmSelection() => EnterTargeting();

    // Phase 1 keys come via the base's tunnelling handler; once we're in phase 2 (no search box) the stage
    // forwards keys here.
    public override void OnKey(KeyEventArgs e)
    {
        if (_targeting) OnTargetingKey(e);
    }

    private void OnTargetingKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Back: Stage?.Back(); e.Handled = true; break; // cancel → back to the map (or hide)
            case Key.Left: Nudge(-1, 0); e.Handled = true; break;
            case Key.Right: Nudge(1, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -1); e.Handled = true; break;
            case Key.Down: Nudge(0, 1); e.Handled = true; break;
            case Key.Enter:
                if (_cursor is { } target)
                {
                    MoveRequested?.Invoke(target, Session.SelectedHwnds);
                    Stage?.CompleteToBase(); // unwind to the map if we opened over it, else dismiss to the desktop
                }
                e.Handled = true;
                break;
        }
    }

    // ── Phase 2: the spatial map ────────────────────────────────────────────────────

    private void EnterTargeting()
    {
        _targeting = true;
        LeavePicker();   // no live previews / search box behind the board
        _cursor = null;  // home onto the origin on the first render
        RenderTargeting();
    }

    // Step the blue cursor to the nearest room in the pressed direction, sharing the map's own resolver so
    // move and the interactive map agree on where each arrow lands.
    private void Nudge(int dx, int dy)
    {
        if (SceneProvider?.Invoke() is not { } sp) return;
        SpatialScene scene = SpatialScene.From(sp.Source, sp.State);
        if (scene.Rooms.Count == 0) return;
        DesktopId cur = _cursor ?? scene.Rooms[0].Id;
        if (SpatialNavigation.NextInDirection(scene, cur, dx, dy) is { } next) _cursor = next;
        RenderTargeting();
    }

    private void RenderTargeting()
    {
        if (SceneProvider?.Invoke() is not { } sp) return;
        (SpatialSource source, SpatialState state) = sp;

        // Home the cursor onto the desktop you're on (the source's selected room) the first time in, and
        // recover if the room it sat on vanished (an external delete since we entered).
        SpatialScene probe = SpatialScene.From(source, state);
        if (_cursor is null || probe.Rooms.All(r => r.Id != _cursor))
            _cursor = probe.Rooms.FirstOrDefault(r => r.Here)?.Id
                   ?? probe.Rooms.FirstOrDefault(r => r.Selected)?.Id
                   ?? probe.Rooms.FirstOrDefault()?.Id;

        // With the cursor injected, it is the blue selection and the desktop you're on becomes the green
        // "here" — so origin and target read apart exactly as they do on the interactive map.
        SpatialScene display = _cursor is { } c ? SpatialScene.From(source, state, c) : probe;

        double width = Stage?.HostWidth ?? 1280, height = Stage?.HostHeight ?? 800;
        Control board = SpatialPainter.Render(display, width, height, Stage?.MapZoom ?? 1.0, new MapCamera(),
                                              style: Stage?.MapStyle ?? MapStyle.Board);

        int n = Session.SelectedCount;
        string dest = _cursor is { } cur && display.Rooms.FirstOrDefault(r => r.Id == cur) is { } room
            ? room.Label : "…";
        Border banner = HintBar($"Moving {n} window{(n == 1 ? "" : "s")} → “{dest}” · " +
                                "←→↑↓ pick a room · Enter to move here · Esc/Backspace cancel");
        banner.VerticalAlignment = VerticalAlignment.Top;
        banner.Margin = new Thickness(0, 24, 0, 0);

        Root.Children.Clear();
        Root.Children.Add(board);
        Root.Children.Add(banner);

        // Re-lift so the board stays visible above the pinned host (mirrors SpatialOverlay.Render).
        Stage?.BringToFront();
    }
}
