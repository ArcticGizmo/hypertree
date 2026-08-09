using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Scopes;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The two-phase "move windows" flow. Phase 1 is the shared <see cref="WindowPickerContent"/> grid — the
/// current desktop's windows, multi-select, now with a search box. Phase 2 (this class's own) reuses
/// <see cref="MapSurface"/> to show the map while the user navigates to a destination, then drops the
/// selected windows there.
///
/// It holds no model: navigation and the move itself are raised as events for <c>App</c> (which owns the
/// <see cref="NavigationModel"/> and desktop controller); the board is pulled via <see cref="BoardProvider"/>.
/// </summary>
internal sealed class MoveContent : WindowPickerContent
{
    private bool _targeting;
    private bool _completed; // a successful drop — so OnRemoved doesn't fire the cancel path

    /// <summary>Supplies the board for phase 2 (App: the live map centred on the move's origin).</summary>
    public Func<NavMap>? BoardProvider;
    /// <summary>A phase-2 arrow — App applies it to the model; we then re-pull the board.</summary>
    public event Action<NavAction>? NavigateRequested;
    /// <summary>Phase-2 Enter — App moves these windows onto the current desktop.</summary>
    public event Action<IReadOnlyList<nint>>? MoveRequested;
    /// <summary>Dismissed without dropping (Esc / Backspace / click-away) — App restores the origin.</summary>
    public event Action? Cancelled;

    public MoveContent(WindowMoveSession session, double initialZoom = 1.0) : base(session, initialZoom) { }

    protected override string PickerHint => "←→↑↓ move · Space tick · Enter choose destination · Esc cancel";
    protected override string EmptyHint => "No windows to move on this desktop · Esc to close";

    public override void OnRemoved()
    {
        base.OnRemoved(); // dispose thumbnails
        if (!_completed) Cancelled?.Invoke(); // Esc / click-away / re-press → restore the origin
    }

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
            case Key.Left: Navigate(NavAction.MoveLeft); e.Handled = true; break;
            case Key.Right: Navigate(NavAction.MoveRight); e.Handled = true; break;
            case Key.Up: Navigate(NavAction.Surface); e.Handled = true; break;
            case Key.Down: Navigate(NavAction.Dive); e.Handled = true; break;
            case Key.Enter:
                _completed = true;
                MoveRequested?.Invoke(Session.SelectedHwnds);
                Stage?.CompleteToBase(); // unwind to the map if we opened over it, else dismiss to the desktop
                e.Handled = true;
                break;
        }
    }

    // Apply the navigation through App (which owns the model), then redraw from the fresh board.
    private void Navigate(NavAction a)
    {
        NavigateRequested?.Invoke(a);
        RenderTargeting();
    }

    // ── Phase 2: the map board ──────────────────────────────────────────────────────

    private void EnterTargeting()
    {
        _targeting = true;
        LeavePicker(); // no live previews / search box behind the board
        RenderTargeting();
    }

    private void RenderTargeting()
    {
        NavMap? map = BoardProvider?.Invoke();
        if (map is null) return;

        double width = Stage?.HostWidth ?? 1280, height = Stage?.HostHeight ?? 800;
        Control board = MapSurface.Render(map, width, height, Stage?.MapStyle ?? MapStyle.Board, Stage?.MapZoom ?? 1.0);

        int n = Session.SelectedCount;
        Border banner = HintBar($"Moving {n} window{(n == 1 ? "" : "s")} · ←→↑↓ navigate · Enter to drop here · Esc/Backspace cancel");
        banner.VerticalAlignment = VerticalAlignment.Top;
        banner.Margin = new Thickness(0, 24, 0, 0);

        Root.Children.Clear();
        Root.Children.Add(board);
        Root.Children.Add(banner);

        // Navigating switched desktops, which can surface that desktop's foreground window above the
        // pinned host — re-lift so the board stays visible (mirrors SpatialOverlay.Render).
        Stage?.BringToFront();
    }
}
