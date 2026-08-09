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
    // ── Reset (implode): the Layouts manager's "restore the empty layout" ───────────

    // Remove every desktop but one and clear all branches — a clean slate. Guarded by a confirm.
    // Mirrors RestoreSnapshot's teardown: stand on the survivor first so the current view is never
    // yanked out from under us, then remove the rest (their windows fall back onto the survivor).
    private void Implode()
    {
        if (_model is null || _desktops is null) return;

        _stage?.Present(new ConfirmContent(
            "Implode all desktops?\nEvery desktop and branch is removed and you’re reset to a single desktop. Windows from the others move onto it. This can’t be undone.",
            DoImplode, confirmLabel: "Implode"));
    }

    private void DoImplode()
    {
        if (_model is null || _desktops is null) return;

        _model.Reconcile(); // act on the live layout, not stale/externally-deleted desktops
        IReadOnlyList<DesktopInfo> all = _desktops.List();
        if (all.Count == 0) return; // nothing to do — never strip the machine to zero desktops

        // Keep the OS's first desktop (the canonical "Desktop 1") as the survivor; everything
        // consolidates onto it.
        DesktopId survivor = all[0].Id;
        _desktops.SwitchTo(survivor);

        foreach (DesktopInfo d in all)
        {
            if (d.Id == survivor) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, survivor); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(0, Array.Empty<Branch>()); // no branches; top row re-derives to the survivor
        RefreshOrFlash();
    }

    // ── Layouts: save / restore / reset the whole desktop+branch arrangement ─────────

    // One manager for whole-layout operations, mirroring the template manager: a palette of saved layouts
    // (each previewing the arrangement it would restore) plus a "Save current layout…" row and a bottom
    // "Reset to a single desktop" row. Enter restores a layout; Del deletes it. Save/delete return here;
    // restore/reset apply and exit to the resulting layout.
    private void LayoutsPrompt() => ShowLayoutManager(refresh: false);

    // <param name="refresh">false: push the manager over the command palette (Esc pops back). true: rebuild
    // it after a save/delete taken on a card pushed over it — the card and the stale list are replaced in
    // place, keeping the command palette beneath (see ReplaceTop).</param>
    private void ShowLayoutManager(bool refresh)
    {
        if (_model is null || _snapshots is null) return;

        var items = new List<PaletteItem>
        {
            new("Save current layout…", "capture these desktops & branches", "＋", () => OpenSaveLayoutCard()),
        };
        foreach (Snapshot snapshot in _snapshots.Load())
        {
            Snapshot s = snapshot; // capture per iteration
            items.Add(new PaletteItem(s.Name, $"{s.DesktopCount} desktops · {s.Branches.Count} branches", "⟲",
                () => ConfirmRestore(s),                          // Enter restores (pushes a confirm over this palette)…
                SpatialPreview: () => SpatialSnapshot.SceneFrom(s), // preview the spatial layout you'd land in
                OnDelete: () => ConfirmDeleteLayout(s)));          // …Del deletes it (pushes a confirm)
        }
        // The reset ("restore the empty layout") sits at the bottom — greyed out when already a single desktop.
        items.Add(new PaletteItem("Reset to a single desktop", "clear every desktop & branch", "⊘",
            Implode, DisabledReason: _model.TotalDesktops <= 1 ? "already a single desktop" : null));

        // Typing a name that matches no saved layout offers to save the current one under it.
        PaletteItem? CreateRow(string q) =>
            new($"Save “{q}”", "save current layout", "＋", () => OpenSaveLayoutCard(prefillName: q));

        var palette = new PaletteContent("Layouts…",
            "↑↓ move · ↵ restore / save · ⌦ delete · Esc back · preview = the saved layout", items, CreateRow);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    // Prompt for a name, then save the current layout under it, and return to the (refreshed) manager.
    private void OpenSaveLayoutCard(string? prefillName = null)
    {
        _stage?.Present(new PromptContent("Save layout",
            "Save the current desktops and branches under a name you can restore to later.",
            "layout name (e.g. before-refactor)",
            name => { SaveSnapshot(name); ShowLayoutManager(refresh: true); },
            confirmLabel: "Save", prefill: prefillName, selectAll: prefillName is not null));
    }

    private void SaveSnapshot(string name)
    {
        if (_model is null || _snapshots is null) return;
        _model.Reconcile(); // capture the live layout, not stale/deleted desktops

        Snapshot snap = _model.CaptureSnapshot(name);
        SpatialSnapshot.Capture(snap, _spatial); // layer the room positions & group colours on top of the structure

        // Same name overwrites, so re-snapshotting a layout updates it in place.
        var list = _snapshots.Load().Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(snap);
        _snapshots.Save(list);
    }

    private void ConfirmRestore(Snapshot snap)
    {
        if (_desktops is null) return;
        _stage?.Present(new ConfirmContent(
            $"Restore layout “{snap.Name}”?\nYour desktops are rebuilt to match it. Desktops that aren’t part of the layout are removed (any windows on them move to another desktop).",
            () => RestoreSnapshot(snap), confirmLabel: "Restore"));
    }

    // Delete a saved layout (Del on its row), then return to the (refreshed) manager.
    private void ConfirmDeleteLayout(Snapshot snap)
    {
        if (_snapshots is null) return;
        _stage?.Present(new ConfirmContent($"Delete layout “{snap.Name}”?", () =>
        {
            var list = _snapshots.Load().Where(s => !s.Name.Equals(snap.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            _snapshots.Save(list);
            ShowLayoutManager(refresh: true); // return to the manager, now without the deleted layout
        }, confirmLabel: "Delete"));
    }

    // Rebuild the OS desktops + the model to match a snapshot. Re-attaches to a saved desktop by its GUID
    // when it still exists, re-creates it by label when it doesn't, then removes anything not in the
    // snapshot. We switch to the snapshot's first desktop BEFORE any removal so the current view can't be
    // yanked out from under us.
    private void RestoreSnapshot(Snapshot snap)
    {
        if (_model is null || _desktops is null) return;
        if (snap.DesktopCount == 0) return; // nothing to restore to — never strip the machine to zero desktops

        _model.Reconcile();
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        var keep = new HashSet<Guid>();

        // Re-keying maps for the spatial facts: a captured desktop GUID → the live GUID it resolved to, and
        // a captured branch id → the fresh id its restored branch minted. Positions/colours ride these over.
        var desktopRemap = new Dictionary<Guid, Guid>();
        var branchRemap = new Dictionary<Guid, Guid>();

        // Resolve one saved desktop to a live id: reuse its GUID if present (renamed to the saved label),
        // else create a fresh desktop with that label. Branch desktops are tracked in _created so the
        // teardown guard may remove them later; main desktops are the user's and are never tracked.
        DesktopId Resolve(PersistedDesktop d, bool branch)
        {
            DesktopId id;
            if (live.Contains(d.Id))
            {
                id = new DesktopId(d.Id);
                if (!string.IsNullOrWhiteSpace(d.Label)) { try { _desktops!.Rename(id, d.Label); } catch { } }
            }
            else
            {
                id = _desktops!.Create(d.Label);
            }
            keep.Add(id.Value);
            desktopRemap[d.Id] = id.Value; // remember where this room's stored position should land
            if (branch) _created.Add(id.Value);
            return id;
        }

        // Main desktops first, so the first one exists to stand on before any removal.
        var mainIds = snap.MainDesktops.Select(d => Resolve(d, branch: false)).ToList();

        var branches = new List<Branch>(snap.Branches.Count);
        foreach (PersistedBranch pg in snap.Branches)
        {
            var refs = pg.Desktops.Select(d => new DesktopRef(Resolve(d, branch: true), d.Label)).ToList();
            if (refs.Count == 0) continue;
            var branch = new Branch(pg.Name, refs, pg.LastUsedIndex); // a template restore mints a fresh id
            branchRemap[pg.Id] = branch.Id; // remember where this group's stored colour should land
            branches.Add(branch);
        }

        // Land on the snapshot's first desktop (main[0], else the first branch desktop) before removing.
        DesktopId first = mainIds.Count > 0 ? mainIds[0] : branches[0].Desktops[0].Id;
        _desktops.SwitchTo(first);

        // Remove every desktop that isn't part of the snapshot; windows fall back to the first desktop.
        foreach (DesktopInfo d in _desktops.List().ToList())
        {
            if (keep.Contains(d.Id.Value)) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, first); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(snap.MainSlot, branches); // re-derives the top row + re-anchors to `first`

        // Re-apply the saved spatial arrangement onto the live state, re-keyed to the ids we just resolved,
        // and persist it — so the restored map lands in its 2-D layout, not the default rows. Then the map
        // rebuild below (RefreshOrFlash → BuildSpatialSource + _spatial) reflects it. Empty tables (an old
        // snapshot, or a never-arranged map) simply write nothing, leaving the defaults in place.
        SpatialSnapshot.ApplyTo(_spatial, snap, desktopRemap, branchRemap);
        _spatialStore?.Save(_spatial);

        RefreshOrFlash();
    }
}
