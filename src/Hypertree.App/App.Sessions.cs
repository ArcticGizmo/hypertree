using Hypertree.App.Views;
using Hypertree.Loadouts;

namespace Hypertree.App;

/// <summary>
/// Loadouts — named workspace definitions (desktops × monitors × commands) built by hand in the graphical
/// <see cref="LoadoutBuilderContent"/> (docs/design/session-restore.md). The "Loadouts…" manager creates, edits
/// and deletes them; applying one to build a branch is <see cref="RestoreLoadout"/> (reached from
/// "Apply loadout…" for now, and from branch creation next). Automatic capture of a running session was
/// removed — manual creation is the model.
/// </summary>
public sealed partial class App
{
    private ILoadoutStore? _loadoutStore;

    // "Loadouts…" — the CRUD manager: build a new loadout, edit or delete a saved one. Mirrors the other
    // managers. refresh:true rebuilds it in place (ReplaceTop) after a builder save or a delete confirm.
    private void ShowLoadoutsManager(bool refresh)
    {
        if (_loadoutStore is null) return;

        var items = new List<PaletteItem>
        {
            new("New loadout…", "build one from scratch", "＋", OpenNewLoadout),
        };
        foreach (Loadout loadout in _loadoutStore.Load().Loadouts)
        {
            Loadout r = loadout; // capture per iteration
            items.Add(new PaletteItem(r.Name, DescribeLoadout(r), "▤",
                () => OpenLoadoutForEdit(r.Name),               // Enter edits it in the builder…
                OnDelete: () => ConfirmDeleteLoadout(r)));       // …Del removes
        }

        var palette = new PaletteContent("Loadouts…",
            "↑↓ move · ↵ edit · ⌦ delete · Esc back", items);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    private static string DescribeLoadout(Loadout r)
    {
        int desktops = r.Desktops.Count, steps = r.StepCount;
        return $"{desktops} desktop{(desktops == 1 ? "" : "s")} · {steps} command{(steps == 1 ? "" : "s")}";
    }

    private void OpenNewLoadout()
    {
        // Start with one empty desktop so there's a monitor grid to fill in immediately.
        var loadout = new Loadout { Name = "", Desktops = { new LoadoutDesktop { Label = "desktop 1" } } };
        OpenBuilder(loadout, replacingName: null);
    }

    private void OpenLoadoutForEdit(string name)
    {
        if (_loadoutStore is null) return;
        if (FindLoadout(_loadoutStore.Load(), name) is not { } loadout) { _stage?.Back(); return; }
        OpenBuilder(loadout, replacingName: name); // the loaded graph is a fresh copy — Cancel just drops it
    }

    // Present the graphical builder over the manager. On save, upsert into the library (removing the old
    // entry, and the pre-rename name too) and rebuild the manager; Cancel (Back) returns to it untouched.
    private void OpenBuilder(Loadout loadout, string? replacingName)
    {
        if (_loadoutStore is null || _desktops is null) return;
        _stage?.Present(new LoadoutBuilderContent(loadout, _desktops.MonitorCount, saved =>
        {
            PersistedLoadouts lib = _loadoutStore.Load();
            lib.Loadouts.RemoveAll(x => x.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)
                                    || (replacingName is not null && x.Name.Equals(replacingName, StringComparison.OrdinalIgnoreCase)));
            lib.Loadouts.Add(saved);
            _loadoutStore.Save(lib);
            ShowLoadoutsManager(refresh: true); // pop the builder + stale manager, show the updated list
        }));
    }

    private void ConfirmDeleteLoadout(Loadout loadout)
    {
        if (_loadoutStore is null) return;
        _stage?.Present(new ConfirmContent($"Delete loadout “{loadout.Name}”?", () =>
        {
            PersistedLoadouts lib = _loadoutStore.Load();
            lib.Loadouts.RemoveAll(x => x.Name.Equals(loadout.Name, StringComparison.OrdinalIgnoreCase));
            _loadoutStore.Save(lib);
            ShowLoadoutsManager(refresh: true);
        }, confirmLabel: "Delete"));
    }

    // Temporary home for "apply a loadout as a new branch" until branch creation absorbs it: a palette of
    // loadouts; choosing one confirms then restores. (Restore itself lives in App.Restore.cs.)
    private void ShowApplyLoadout()
    {
        if (_loadoutStore is null) return;
        var items = _loadoutStore.Load().Loadouts.Select(loadout =>
        {
            Loadout r = loadout;
            return new PaletteItem(r.Name, DescribeLoadout(r), "▶", () => BeginApply(r));
        }).ToList();

        if (items.Count == 0)
            items.Add(new PaletteItem("No loadouts yet", "build one in “Loadouts…” first", null, () => _stage?.Back(),
                                      DisabledReason: "build one in “Loadouts…” first"));

        _stage?.Present(new PaletteContent("Apply loadout as a new branch…",
            "↑↓ move · ↵ apply · Esc back", items));
    }

    private static Loadout? FindLoadout(PersistedLoadouts lib, string name) =>
        lib.Loadouts.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
