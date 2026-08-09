using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Ipc;
using Hypertree.App.Status;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Changelog;
using Hypertree.Desktops;
using Hypertree.Ipc;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

public sealed partial class App
{
    // Navigate. While the map overlay is open it stays open (its windows are pinned across the
    // desktop switch) and re-homes its selection onto the desktop we land on; otherwise the flash shows.
    // <paramref name="mods"/> is the chord's modifier layer — the flash holds while these are down.
    // With "show before moving" on (the default), the press that raises the flash doesn't also move —
    // see <see cref="RevealOnly"/>.
    private void Navigate(NavAction action, HotkeyModifiers mods)
    {
        if (_model is null || _desktops is null) return;
        // The move flow owns the arrows while it's up (its own plain-arrow handlers drive it), so an
        // out-of-habit nav chord mustn't also navigate underneath.
        if (_stage?.Current is MoveContent) return;
        // Something outside Hypertree may have moved us since our last navigation (another launcher
        // jumping to a window, Task View, Win+Ctrl+Arrow). Start this move from where we actually are,
        // not from where our own cursor was left. Mid-gesture it's a no-op — we're already standing on
        // the desktop the previous keystroke switched to.
        _model.AnchorToCurrent();
        // Start of a gesture: remember where we came from (and which modifiers to watch), so releasing
        // them can record it as "last visited". A poll watches for the release (flashing or in the map).
        _gestureFrom ??= _desktops.Current;
        _gestureMods = mods;

        bool softMotion = WindowFx.SystemAnimationsEnabled();
        bool animate = _settings.AnimateNavigation && softMotion;
        bool inMap = AnyMapOpen();

        // The direction resolves to the nearest room; "crossing a group" is the analog of a dive/surface —
        // it's what the show-before-moving reveal keys off.
        (int dx, int dy) = DirectionOf(action);
        SpatialScene pre = SpatialScene.From(_model.BuildSpatialSource(), _spatial);
        DesktopId? target = SpatialNavigation.NextInDirection(pre, _desktops.Current, dx, dy);
        bool crossing = target is { } t &&
                        SpatialNavigation.GroupOf(pre, t) != SpatialNavigation.GroupOf(pre, _desktops.Current);

        // A cold press with "show before moving" on only raises the board — it doesn't move (a plain fade,
        // no wipe). It applies to a move out of the current group; a move that stays in it goes straight away,
        // and the map never needs the reveal.
        bool revealOnly = _settings.DisplayBeforeMoving && crossing && !inMap && _hud is { IsVisible: false };

        // Cover the screen BEFORE switching, so the switch happens behind the dim rather than flashing the lit
        // destination desktop for a beat. The map is its own always-up surface, and a reveal doesn't switch.
        if (!inMap && !revealOnly) _hud?.Cover();

        // Apply the move. It reports whether the desktop actually changed (false at an edge / already there),
        // so a move that goes nowhere doesn't play a directional wipe.
        bool moved = !revealOnly && target is { } tid && JumpToId(tid);

        // In the map, the nav chord switches for real and the selection follows; otherwise the flash shows,
        // with the green marker on the gesture origin so direction/distance reads at a glance.
        if (inMap) SyncOpenMapToCurrent();
        else FlashBoard(_gestureFrom, mods, moved ? action : null, animate && moved, softMotion);
        StartGesturePoll();
    }

    private static (int X, int Y) DirectionOf(NavAction action) => action switch
    {
        NavAction.MoveLeft => (-1, 0),
        NavAction.MoveRight => (1, 0),
        NavAction.Surface => (0, -1),
        NavAction.Dive => (0, 1),
        _ => (0, 0),
    };

    // Raise the transient flash on the current spatial arrangement. <paramref name="cameFrom"/> marks the
    // gesture origin with the green "here" outline (null for a plain result/peek flash).
    private void FlashBoard(DesktopId? cameFrom, HotkeyModifiers mods, NavAction? move, bool animate, bool fade)
    {
        if (_hud is null || _model is null) return;
        SpatialScene scene = SpatialScene.From(_model.BuildSpatialSource(cameFrom), _spatial);
        _hud.Flash(scene, _settings.MapStyle, mods, move, animate, _settings.SweepFromLeadingEdge, fade);
    }

    // Peek: raise the flash on where we actually are and hold it while <paramref name="mods"/> stay down,
    // without moving. A preview on demand — and, since the board is up afterwards, a following nav chord
    // moves for real (the same hand-off as "show before moving", but triggered explicitly and regardless of
    // that setting). No gesture is recorded: nothing moved, so there's no "last visited" to remember.
    private void Peek(HotkeyModifiers mods)
    {
        if (_model is null || _desktops is null) return;
        // The move flow owns the arrows while it's up; the map is already a persistent board — neither wants
        // a transient peek over it.
        if (_stage?.Current is MoveContent) return;
        if (AnyMapOpen()) return;
        _model.AnchorToCurrent(); // show where we stand now, not our stale cursor
        // A peek has no direction, so there's nothing to wipe — it's pure appearance, and fades up.
        FlashBoard(null, mods, move: null, animate: false, fade: WindowFx.SystemAnimationsEnabled());
    }

    // A double-click / arrow-driven jump from the map: switch to the chosen desktop, record where we
    // came from, then re-home the selection onto it (green + blue rejoin), keeping the map open.
    private void JumpFromMap(Func<bool> doJump)
    {
        if (_desktops is null || _model is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        RecordVisit(from);
        SyncOpenMapToCurrent();
    }

    private void StartGesturePoll()
    {
        if (_gesturePoll is null)
        {
            _gesturePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _gesturePoll.Tick += (_, _) => { if (!ModifierKeys.ModifiersHeld(_gestureMods)) CompleteGesture(); };
        }
        if (!_gesturePoll.IsEnabled) _gesturePoll.Start();
    }

    // The gesture is over once Ctrl+Alt is released: if we actually moved, the desktop we started on
    // becomes "last visited" and the whole gesture lands on the trail as one crumb — the transaction's
    // end point, not every desktop it stepped through.
    private void CompleteGesture()
    {
        _gesturePoll?.Stop();
        if (_gestureFrom is { } from) RecordVisit(from);
        _gestureFrom = null;
    }

    // ── Navigation history (breadcrumb trail · Ctrl+Alt+A back / S forward / Q flip) ──

    // One navigation transaction ended: we set out from `from` and settled wherever the OS now says.
    // A no-op when we ended up back where we started. This is the ONLY place the trail is written, so
    // every crumb is a transaction's end point — intermediate steps and undo/redo moves never land on it.
    private void RecordVisit(DesktopId from)
    {
        if (_desktops is null || _desktops.Current == from) return;
        _lastVisited = from;
        _history.Record(from, _desktops.Current);
        RefreshOverlay(); // the map's history panel shows the new crumb without waiting for the next render
    }

    // Walk the trail: back to the previous transaction's end point, or forward again. The trail itself is
    // not rewritten — only a real navigation does that (truncating the forward tail; see NavHistory.Record).
    // <paramref name="mods"/> is the chord's modifier layer, so the flash holds while it's held, exactly
    // like a navigation keystroke.
    private void StepHistory(bool back, HotkeyModifiers mods)
    {
        if (PrepareHistoryJump() is not { } cur) return;

        DesktopId? target;
        if (back && _history.Current is { } tip && tip != cur)
        {
            // We've wandered off the trail (an external switch, or a gesture still in flight) — "back"
            // first returns to where the trail stands, without consuming an undo step.
            target = tip;
        }
        else
        {
            target = back ? _history.Undo() : _history.Redo();
            // Pruning can leave the neighbouring entry equal to where we already are — step past it.
            while (target is { } same && same == cur) target = back ? _history.Undo() : _history.Redo();
        }
        JumpAlongTrail(target, cur, mods);
    }

    // Ctrl+Alt+Q — bounce between the trail's two newest entries (the alt-tab of desktops): press to hop
    // to the other one, press again to hop back, for ever. NavHistory.Toggle picks the target and parks
    // the cursor on it, so the map's panel follows and a real navigation branches from where the hop left you.
    private void ToggleHistory(HotkeyModifiers mods)
    {
        if (PrepareHistoryJump() is not { } cur) return;
        JumpAlongTrail(_history.Toggle(cur), cur, mods);
    }

    // Shared guard + freshen for every history jump. Work against the live layout: drop desktops deleted
    // behind our back from both the model and the trail. Returns where we stand, or null when a history
    // jump can't run right now (still starting up, or the move flow owns navigation).
    private DesktopId? PrepareHistoryJump()
    {
        if (_model is null || _desktops is null) return null;
        if (_stage?.Current is MoveContent) return null;
        _model.Reconcile();
        _history.Prune(id => _model.Locate(id) is not null);
        return _desktops.Current;
    }

    // Switch to a desktop the trail picked, presenting it like a navigation: the open map follows the
    // switch; otherwise the board flashes with the origin marked green. No wipe — a history jump has no
    // row/column direction to carry. A null / untracked target is a quiet no-op.
    private void JumpAlongTrail(DesktopId? target, DesktopId cur, HotkeyModifiers mods)
    {
        if (_model is null || target is not { } id || _model.Locate(id) is not { } at) return;

        if (at.onMain) _model.GoToTop(at.desktopIndex);
        else _model.GoToBranchDesktop(at.branchIndex, at.desktopIndex);

        if (AnyMapOpen()) SyncOpenMapToCurrent();
        else FlashBoard(cur, mods, move: null, animate: false, fade: WindowFx.SystemAnimationsEnabled());
    }

    // A discrete jump from the spotlight palette: switch, record where we came from, then close the overlay
    // outright — a jump physically moves you, so it's terminal (you don't return to the map behind it).
    private void Jump(Func<bool> doJump)
    {
        if (_desktops is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        RecordVisit(from);
        _stage?.Dismiss();
    }
}
