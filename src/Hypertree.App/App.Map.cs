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
    private void ToggleMap()
    {
        if (_model is null || _spatialOverlay is null || _desktops is null) return;
        if (_spatialOverlay.IsOpen) { _spatialOverlay.Close(); return; }

        _model.Reconcile(); // drop any externally-deleted desktops before showing the map
        OpenMap();
    }

    // Open the spatial map. Reframe first, since its metrics differ from the flash offset the shared camera
    // may hold.
    private void OpenMap()
    {
        if (_model is null) return;
        _mapCamera.Reframe();
        SpatialSource source = _model.BuildSpatialSource();
        PruneSpatial(source); // forget positions for desktops that no longer exist, so spatial.json doesn't grow
        _spatialOverlay?.Open(source, _spatial);
    }

    // Drop stored positions for desktops the source no longer contains (deleted since they were placed).
    // GUIDs are unique so a stale entry would never wrongly match, but this keeps the file from growing.
    private void PruneSpatial(SpatialSource source)
    {
        if (_spatialStore is null) return;
        var live = source.Groups.SelectMany(g => g.Desktops).Select(d => d.Id.Value.ToString()).ToHashSet();
        int removed = _spatial.Positions.Keys.Where(k => !live.Contains(k)).ToList()
            .Count(k => _spatial.Positions.Remove(k));
        if (removed > 0) _spatialStore.Save(_spatial);
    }

    // Resolve a spatial jump: turn a desktop id into the row/branch position the model jumps by.
    private bool JumpToId(DesktopId id)
    {
        if (_model is null) return false;
        if (_model.Locate(id) is not { } at) return false;
        return at.OnMain ? _model.GoToTop(at.DesktopIndex)
                         : _model.GoToBranchDesktop(at.BranchIndex, at.DesktopIndex);
    }

    private bool AnyMapOpen() => _spatialOverlay is { IsOpen: true };

    // Re-home the open map onto the desktop we're now on — after a real switch.
    private void SyncOpenMapToCurrent()
    {
        if (_model is null) return;
        if (_spatialOverlay is { IsOpen: true }) _spatialOverlay.SyncToCurrent(_model.BuildSpatialSource(), _spatial);
    }

    // Prime the map with a fresh board: redraws now if it's the current surface, else stashes it so the
    // map shows the update the next time the stage unwinds back to it (after an action completes on a card).
    // SetSource itself decides render-now vs stash, so this must NOT be gated on IsOpen — an action run from
    // a card on top of the map (e.g. the group picker) leaves the map not-current, and gating here would drop
    // the update so the map re-presents stale.
    private void RefreshOverlay()
    {
        if (_model is null) return;
        _spatialOverlay?.SetSource(_model.BuildSpatialSource(), _spatial);
    }

    // ── Set a room's group (g on the spatial map) ───────────────────────────────────

    // g on the spatial map: pick the group (branch) the highlighted room belongs to, or type a new name to
    // create one. Reuses the shared palette — its "create «name»" row is exactly the "create xxxx" affordance
    // — over the durable spatial map, so choosing reassigns the desktop and unwinds back to the recoloured
    // map. The room's current group is left out of the list (you can't move it to where it already is).
    private void OpenGroupPickerForRoom(DesktopId id)
    {
        if (_model is null || _stage is null) return;
        SpatialSource source = _model.BuildSpatialSource();
        SpatialGroupSource? owner = source.Groups.FirstOrDefault(g => g.Desktops.Any(d => d.Id == id));
        if (owner is null) return; // the room vanished (e.g. an external delete) — nothing to regroup

        string room = owner.Desktops.First(d => d.Id == id).Label;

        var items = new List<PaletteItem>();
        foreach (SpatialGroupSource g in source.Groups)
        {
            if (g.Id == owner.Id) continue;                  // skip the group it's already in
            Guid target = g.Id;
            int n = g.Desktops.Count;
            items.Add(new PaletteItem(
                g.IsMain ? "main" : g.Name,
                n == 1 ? "1 room" : $"{n} rooms",
                g.IsMain ? "○" : "●",
                () => AssignRoomToGroup(id, target)));
        }

        // A typed name that matches no existing group offers to create it, seeded with the moved room.
        PaletteItem? CreateRow(string q) =>
            new($"Create “{q}”", "new group", "＋", () => AssignRoomToNewGroup(id, q));

        _stage.Present(new PaletteContent(
            $"Set group for “{room}”…",
            "↑↓ move · ↵ choose · type a name to create · Esc back",
            items, CreateRow));
    }

    private void AssignRoomToGroup(DesktopId id, Guid groupId)
    {
        if (_model is null) return;
        _model.MoveDesktopToGroup(id, groupId);
        RefreshOverlay(); // stash the regrouped board; the stage re-presents the map as the palette completes
    }

    private void AssignRoomToNewGroup(DesktopId id, string name)
    {
        if (_model is null) return;
        name = name.Trim();
        if (name.Length == 0) return;
        _model.MoveDesktopToNewBranch(id, name);
        RefreshOverlay();
    }

    // ── Move windows to another desktop (m on the map) ──────────────────────────────

    // Phase 1: snapshot the current desktop's windows and open the picker. Re-press toggles it closed.
    private void ToggleMoveWindows()
    {
        if (_model is null || _desktops is null || _stage is null) return;
        if (_stage.Current is MoveContent) { _stage.Back(); return; } // re-press cancels (via OnRemoved)

        _model.Reconcile();
        _moveOrigin = _desktops.Current;
        var session = new WindowMoveSession(_desktops.WindowsOn(_moveOrigin.Value));

        // Presenting the move content on the stage swaps out any open map/palette in place. Navigation
        // and the move are serviced here; the board is pulled live from the model, centred on the origin.
        var content = new MoveContent(session, _settings.PickerZoom) { BoardProvider = () => _model!.BuildMap(_moveOrigin) };
        content.NavigateRequested += a => _model!.Apply(a);
        content.MoveRequested += MoveSelectedWindows;
        content.Cancelled += CancelMove;
        content.ZoomChanged += PersistPickerZoom; // Ctrl+/Ctrl− — remember the thumbnail size
        // Launched from the map → push over it so completing/cancelling unwinds back to the map (its durable
        // base). Otherwise (hotkey / tray, no map open) it's a fresh root that dismisses to the desktop —
        // Back/CompleteToBase then behave exactly like the old Dismiss.
        if (_spatialOverlay?.IsOpen == true) _stage.Present(content);
        else _stage.Summon(content);
    }

    // Phase 2 commit: we've navigated to the destination (it's the current desktop), so move each
    // selected window there. The content dismisses the stage itself; we stay on the destination.
    private void MoveSelectedWindows(IReadOnlyList<nint> hwnds)
    {
        if (_desktops is null) return;
        DesktopId dest = _desktops.Current;
        foreach (nint h in hwnds)
        {
            try { _desktops.MoveWindowToDesktop(h, dest); } catch { /* window may have closed — best-effort */ }
        }
        _moveOrigin = null;
        RefreshOrFlash(); // flash the destination with its now-updated window counts
    }

    // Cancel (Esc / Backspace / click-away / re-press) — raised from the move content's OnRemoved as the
    // stage dismisses it: return to where the move started (phase 2 may have navigated us away) and
    // re-anchor the model there.
    private void CancelMove()
    {
        if (_desktops is not null && _moveOrigin is { } origin && _desktops.Current != origin)
            _desktops.SwitchTo(origin);
        _moveOrigin = null;
        _model?.Resync();
        RefreshOverlay(); // if we opened over the map, it's about to be re-presented — give it a fresh board
    }

    // ── Pull windows from other desktops onto this one (Shift+m on the map) ─────────

    // Snapshot every window on the other desktops and open the picker. Re-press toggles it closed. Unlike
    // move there's no second phase and no origin to restore: the destination is always where we already are.
    private void TogglePullWindows()
    {
        if (_model is null || _desktops is null || _stage is null) return;
        if (_stage.Current is PullContent) { _stage.Back(); return; } // re-press cancels

        _model.Reconcile();
        var session = new WindowMoveSession(_desktops.WindowsElsewhere());
        var content = new PullContent(session, _settings.PickerZoom);
        content.PullRequested += PullSelectedWindows;
        content.ZoomChanged += PersistPickerZoom; // Ctrl+/Ctrl− — remember the thumbnail size
        // Launched from the map → push over it so completing/cancelling unwinds back to the map. Otherwise
        // (hotkey / tray, no map open) it's a fresh root that dismisses to the desktop.
        if (_spatialOverlay?.IsOpen == true) _stage.Present(content);
        else _stage.Summon(content);
    }

    // Commit: move each selected window onto the current desktop (where we already are). The content
    // dismisses the stage itself; we stay put and flash the now-updated window counts.
    private void PullSelectedWindows(IReadOnlyList<nint> hwnds)
    {
        if (_desktops is null) return;
        DesktopId here = _desktops.Current;
        foreach (nint h in hwnds)
        {
            try { _desktops.MoveWindowToDesktop(h, here); } catch { /* window may have closed — best-effort */ }
        }
        RefreshOrFlash();
    }

    // Both picker flows share one persisted thumbnail size (settings.PickerZoom), so a zoom set in "move"
    // carries over to "pull" and survives a restart — mirrors how the map's zoom is remembered.
    private void PersistPickerZoom(double zoom)
    {
        _settings.PickerZoom = zoom;
        _settingsStore?.Save(_settings);
    }

    // ── Manage-map actions (r / Del / Shift+Del / n) ───────────────────────────────

    // r on the map: open the rename prompt prefilled with the selected desktop's current name. On confirm,
    // rename the OS desktop and the model's stored label, then refresh the map in place. The prompt steals
    // focus (it's a top-most window); when it closes we hand the stage its key focus back so the arrow
    // selection resumes.
    private void RenameSelected(DesktopSelection sel)
    {
        if (_model is null || _desktops is null) return;

        var peek = sel.OnMain
            ? _model.PeekTopDesktop(sel.DesktopIndex)
            : _model.PeekBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
        if (peek is null) return;

        // A card over the map, prefilled + select-all so the first keystroke replaces the name. On confirm
        // the model is relabelled and the map primed; CompleteToBase then unwinds to the map, now relabelled.
        _stage?.Present(new PromptContent("Rename desktop",
            "Type a new name for this desktop.", "desktop name",
            name =>
            {
                try { _desktops.Rename(peek.Value.id, name); } catch { /* best-effort — desktop may have gone */ }
                _model.SetDesktopLabel(sel.OnMain, sel.BranchIndex, sel.DesktopIndex, name);
                RefreshOverlay();
            },
            confirmLabel: "Rename", prefill: peek.Value.label, selectAll: true));
    }

    // Shift+R on the map: rename the branch at `index` (main has no branch, so the overlay never raises this
    // for it). A card over the map, prefilled + select-all; on confirm the model relabels, persists and the
    // map redraws with the new branch name.
    private void RenameBranchOnMap(int index)
    {
        if (_model is null) return;
        if (_model.BranchNameAt(index) is not { } current) return;

        _stage?.Present(new PromptContent("Rename branch",
            "Type a new name for this branch.", "branch name",
            name =>
            {
                _model.RenameBranch(index, name);
                RefreshOverlay();
            },
            confirmLabel: "Rename", prefill: current, selectAll: true));
    }

    // Del on the map: delete the selected desktop (with a confirm), resolving main vs. branch.
    private void DeleteSelectedDesktop(DesktopSelection sel)
    {
        if (sel.OnMain) DeleteTopDesktop(sel.DesktopIndex);
        else DeleteBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
    }

    // Shift+Del on the map: delete an entire branch (all its desktops) behind a confirm.
    private void ConfirmRemoveBranch(int index)
    {
        if (_model is null) return;
        var map = _model.BuildMap();
        if (index < 0 || index >= map.Branches.Count) return;
        NavMapBranch g = map.Branches[index];
        Confirm($"Delete branch “{g.Name}”?\nIts {g.Desktops.Count} desktop{(g.Desktops.Count == 1 ? "" : "s")} " +
                "are removed and any windows on them move to another desktop.", () => RemoveBranch(index));
    }

    // n on the map: prompt for a name, create a new desktop at the end of the selected row (no switch — the
    // manage surface stays where you are), then home the selection onto it so you can rename/act on it
    // immediately. The row is the point: pressing n inside a branch grows *that* branch, which is where you
    // were already looking, rather than dropping the desktop onto main for you to drag back up.
    private void PromptNewDesktop(DesktopSelection sel)
    {
        if (_model is null || _desktops is null) return;

        // Resolved before the prompt opens, so the card can say where the desktop will land.
        string? branch = sel.OnMain ? null : _model.BranchNameAt(sel.BranchIndex);

        _stage?.Present(new PromptContent("New desktop",
            $"Create a new desktop on {(branch is null ? "the main timeline" : $"branch “{branch}”")}. " +
            "You stay on the current desktop.",
            "desktop name (e.g. email)",
            name =>
            {
                if (branch is null)
                {
                    DesktopId mainId = _desktops.Create(name); // a main-timeline desktop is the user's own — not tracked in _created
                    _model.SyncTopRow();    // picks up the new desktop (appended to the top row)
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(mainId); // home the cursor to the just-created room
                    return;
                }

                // In a branch: the OS name carries the branch prefix (as branch creation does) while the tile
                // keeps the bare label, and it's tracked in _created so teardown can clean it up with the rest
                // of the branch.
                DesktopId id = _desktops.Create($"{branch} · {name}");
                _created.Add(id.Value);
                if (_model.AddDesktopToBranch(sel.BranchIndex, new DesktopRef(id, name)) is not null)
                {
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(id);
                }
                else
                {
                    // The branch went away while the prompt was open (deleted, or dissolved when its last
                    // desktop moved out). The desktop exists, so let it show up on main rather than stranding it.
                    _created.Remove(id.Value);
                    _model.SyncTopRow();
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(id);
                }
            },
            confirmLabel: "Create"));
    }

    // ── Spotlight: jump to any existing desktop, or create one named the query ─────

    // Pushed over whatever opened it (the map via Ctrl+F, or the command palette's "Jump to desktop…"
    // row), so Esc always pops straight back there.
    private void OpenSpotlight()
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any desktops deleted out from under us before offering jumps
        NavMap map = _model.BuildMap();
        var items = new List<PaletteItem>();
        int lastIndex = -1; // the last-visited row, to float to the top

        // Is this desktop the last-visited one? Decorated with "(last)" + a ↩ icon and moved first.
        bool IsLast(DesktopId? id) => _lastVisited is { } lv && id is { } tid && tid == lv;
        string Detail(string ctx, bool last) => last ? $"{ctx} · (last)" : ctx;
        string Icon(bool last) => last ? "↩" : "→";

        // Every main-timeline desktop, then every branch's desktops (branch name in the detail so
        // typing a branch name filters to its desktops). Each carries a Preview board that highlights
        // where the jump would land, shown in the middle of the palette as you move the selection.
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            int idx = i;
            DesktopId? tid = _model.PeekTopDesktop(i)?.id;
            bool last = IsLast(tid);
            items.Add(new PaletteItem(map.TopRow[i].Label, Detail("main", last), Icon(last),
                () => Jump(() => _model!.GoToTop(idx)), // no flash — the preview already showed it
                Preview: () => PreviewMap(onMain: true, topIndex: idx, branchIndex: -1, desktopIndex: -1),
                SpatialPreview: tid is { } t ? () => SpatialPreviewScene(t) : null));
            if (last) lastIndex = items.Count - 1;
        }
        foreach (NavMapBranch g in map.Branches)
        {
            int gi = g.Index;
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                int dj = j;
                DesktopId? tid = _model.PeekBranchDesktop(gi, dj)?.id;
                bool last = IsLast(tid);
                items.Add(new PaletteItem(g.Desktops[j].Label, Detail(g.Name, last), Icon(last),
                    () => Jump(() => _model!.GoToBranchDesktop(gi, dj)),
                    Preview: () => PreviewMap(onMain: false, topIndex: -1, branchIndex: gi, desktopIndex: dj),
                    SpatialPreview: tid is { } t ? () => SpatialPreviewScene(t) : null));
                if (last) lastIndex = items.Count - 1;
            }
        }

        // Float the last-visited desktop to the top so it's the default (empty-query) selection.
        if (lastIndex >= 0)
        {
            PaletteItem lastItem = items[lastIndex];
            items.RemoveAt(lastIndex);
            items.Insert(0, lastItem);
        }

        OpenPalette("Jump to or create a desktop…",
            "↑↓ move · ↵ jump/create · Esc back · blue = you are here", items,
            query => new PaletteItem($"Create desktop “{query}”", "new · main", "+",
                () => CreateAndGoToDesktop(query))); // no target tile yet — the stage shows the live board
    }

    // The spatial twin of PreviewMap: the current scene with the jump's target as the blue selection (and
    // the desktop you're on staying the green "here"), so the jump palette highlights where you'd land as a
    // room while the user is in the spatial model.
    private SpatialScene SpatialPreviewScene(DesktopId target)
        => SpatialScene.From(_model!.BuildSpatialSource(), _spatial, target);

    // Build a board snapshot that marks a specific desktop as current (for the jump palette's preview),
    // without moving the model. Rebuilds the tiles from the live map with the target highlighted and
    // centred on its own row.
    // Build a preview board for the jump palette. IsCurrent (blue) marks where you ARE now; IsHere
    // (green) marks the selected target (which defaults to the last-visited desktop). The board is
    // centred on your current position, so the green target shows the direction/distance of the jump.
    // (onMain/topIndex/branchIndex/desktopIndex describe the target row.)
    private NavMap PreviewMap(bool onMain, int topIndex, int branchIndex, int desktopIndex)
    {
        NavMap b = _model!.BuildMap();

        bool hereMain = _model.OnTop;
        int hereTop = _model.CurrentTopIndex;
        (int hereBranch, int hereDesktop) = _model.CurrentBranchDesktop ?? (-1, -1);

        var top = b.TopRow.Select((t, i) => new NavMapTile(
            t.Label,
            hereMain && i == hereTop,      // IsCurrent (blue) = you are here
            onMain && i == topIndex,       // IsHere (green) = the target
            t.WindowCount)).ToList();      // keep the at-a-glance count on the preview board
        var branches = b.Branches.Select(g => new NavMapBranch(
            g.Index, g.Name,
            g.Desktops.Select((d, j) => new NavMapTile(
                d.Label,
                !hereMain && g.Index == hereBranch && j == hereDesktop,   // blue = current
                !onMain && g.Index == branchIndex && j == desktopIndex,   // green = target
                d.WindowCount)).ToList(),
            // Keep both the current branch and the target branch bright (undimmed).
            (!hereMain && g.Index == hereBranch) || (!onMain && g.Index == branchIndex),
            g.Index == hereBranch ? hereDesktop : g.Index == branchIndex ? desktopIndex : g.Cursor)).ToList();
        int topCursor = hereMain ? hereTop : b.TopCursor;
        return new NavMap(top, topCursor, hereMain, branches, b.TopPosition);
    }

    // Create a new unbranched desktop named the query and jump straight to it.
    private void CreateAndGoToDesktop(string name)
    {
        if (_model is null || _desktops is null) return;
        DesktopId from = _desktops.Current;
        DesktopId id = _desktops.Create(name);
        _created.Add(id.Value);
        _model.SyncTopRow();
        _desktops.SwitchTo(id);
        _model.Resync(); // land the model on the freshly-created desktop
        RecordVisit(from);
        _stage?.Dismiss(); // decisive: you're now on the new desktop, so close the overlay
    }

    // Push a palette over the current surface (or as a fresh root when nothing is showing). Esc pops back.
    private void OpenPalette(string placeholder, string hint, IReadOnlyList<PaletteItem> items,
                             Func<string, PaletteItem?>? createRow = null)
    {
        _stage?.Present(new PaletteContent(placeholder, hint, items, createRow));
    }

    // ── Spatial map wiring ──────────────────────────────────────────────────────────

    // Create the spatial map overlay and connect its keyboard-driven edit requests to the App command that
    // services each. Called once from Startup. The map raises a DesktopId (a room) or a Guid (a group);
    // WithSelection / WithBranch resolve those to the position-based ops the commands take, so the glue here
    // stays one line each.
    private void WireSpatialOverlay()
    {
        _spatialOverlay = new SpatialOverlay(_stage!, _mapCamera, _settings.MapZoom, _settings.ShowMapLegend);
        _spatialOverlay.JumpRoomRequested += id => JumpFromMap(() => JumpToId(id));
        _spatialOverlay.ViewStyleToggleRequested += ToggleMapStyle; // v — cycle board ↔ metro ↔ ascii (app-wide)
        _spatialOverlay.SpatialStateChanged += () => _spatialStore?.Save(_spatial); // a move or recolour is written to spatial.json
        _spatialOverlay.ZoomChanged += zoom =>                      // +/− — persist the map zoom and mirror it to
        {                                                          // every other surface that draws the map (flash, backdrops, move flow)
            _settings.MapZoom = zoom;
            _settingsStore?.Save(_settings);
            if (_stage is not null) _stage.MapZoom = zoom;
            if (_hud is not null) _hud.MapZoom = zoom;
        };
        _spatialOverlay.LegendVisibilityChanged += show => { _settings.ShowMapLegend = show; _settingsStore?.Save(_settings); }; // l — persist the legend
        _spatialOverlay.SetRoomGroupRequested += OpenGroupPickerForRoom;            // g — pick / create the room's group
        _spatialOverlay.DeleteRoomRequested += id => WithSelection(id, DeleteSelectedDesktop); // Del — confirm/teardown
        _spatialOverlay.DeleteGroupRequested += g => WithBranch(g, ConfirmRemoveBranch);        // Shift+Del — a group is a branch
        _spatialOverlay.RenameRoomRequested += id => WithSelection(id, RenameSelected);         // r — rename the desktop
        _spatialOverlay.RenameGroupRequested += g => WithBranch(g, RenameBranchOnMap);          // Shift+R — rename the branch
        _spatialOverlay.NewDesktopRequested += id => WithSelection(id, PromptNewDesktop);       // n — new desktop in the room's group
        _spatialOverlay.NewBranchRequested += () => OpenNewBranchDialog(null);      // b — branch card over the map
        _spatialOverlay.MoveWindowsRequested += ToggleMoveWindows;                  // m — move this desktop's windows elsewhere
        _spatialOverlay.PullWindowsRequested += TogglePullWindows;                  // Shift+m — pull windows onto this desktop
        _spatialOverlay.FinderRequested += OpenSpotlight;                           // f — finder over the map; Esc pops back
        _spatialOverlay.CommandPaletteRequested += () => ShowCommandPalette(overCurrent: true); // p — palette over the map
        _spatialOverlay.AppLauncherRequested += () => OpenAppLauncher(overCurrent: true);        // o — launcher over the map
    }

    // Resolve a room's DesktopId to its position-based selection and run <paramref name="act"/>; a no-op if
    // the room is gone (e.g. an external delete since the map was drawn).
    private void WithSelection(DesktopId id, Action<DesktopSelection> act)
    {
        if (_model?.Locate(id) is { } at) act(new DesktopSelection(at.OnMain, at.BranchIndex, at.DesktopIndex));
    }

    // Resolve a group's stable id to its branch index and run <paramref name="act"/>; a no-op if no branch
    // carries that id.
    private void WithBranch(Guid groupId, Action<int> act)
    {
        int i = _model?.IndexOfBranch(groupId) ?? -1;
        if (i >= 0) act(i);
    }
}
