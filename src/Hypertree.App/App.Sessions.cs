using Hypertree.App.Views;
using Hypertree.Launch;
using Hypertree.Recipes;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// Sessions as <b>recipes</b> (docs/design/session-restore.md): capture what's open on a branch's desktops
/// into a named, inspectable recipe — an ordered set of desktops, each with its launch-and-place steps.
/// This is Phase A: save the current branch as a recipe, and inspect / delete saved ones. Restore (the
/// staging executor that relaunches a recipe and places each window) lands in Phase B.
/// </summary>
public sealed partial class App
{
    // The saved recipe library (recipes.json). Generated recipes and, later, hand-authored ones share it.
    private IRecipeStore? _recipeStore;

    // "Sessions…" — the recipe manager: save the current branch, inspect a saved recipe's desktops/steps,
    // or delete one. Mirrors the Layouts / Templates managers.
    // refresh:false pushes it over the command palette (Esc pops back); refresh:true rebuilds it in place
    // after a delete taken on a confirm card pushed over it (ReplaceTop drops the card + stale manager).
    private void ShowSessionsManager(bool refresh)
    {
        if (_recipeStore is null) return;

        BranchView? branch = _model?.CurrentBranchView();

        // Save-the-current-branch row, greyed on the main timeline where there's no branch to capture.
        var items = new List<PaletteItem>
        {
            branch is { } b
                ? new PaletteItem($"Save “{b.Name}” as a recipe", "capture what's open on this branch", "＋",
                                  () => SaveBranchAsRecipe(b))
                : new PaletteItem("Save this branch as a recipe", "dive into a branch first", "＋",
                                  () => { }, DisabledReason: "dive into a branch first"),
        };

        foreach (Recipe recipe in _recipeStore.Load().Recipes)
        {
            Recipe r = recipe; // capture per iteration
            items.Add(new PaletteItem(r.Name, DescribeRecipe(r), "▤",
                () => InspectRecipe(r),                     // Enter inspects…
                OnDelete: () => ConfirmDeleteRecipe(r)));    // …Del removes
        }

        var palette = new PaletteContent("Sessions…",
            "↑↓ move · ↵ inspect · ⌦ delete · Esc back", items);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    private static string DescribeRecipe(Recipe r)
    {
        int desktops = r.Desktops.Count, steps = r.StepCount;
        return $"{desktops} desktop{(desktops == 1 ? "" : "s")} · {steps} app{(steps == 1 ? "" : "s")}";
    }

    // Generate a recipe from the branch's live desktops and save it under the branch name — overwriting any
    // recipe of the same name, so re-saving a branch updates its recipe in place. Terminal: a notification
    // confirms and the palette unwinds (reopen "Sessions…" to inspect).
    private void SaveBranchAsRecipe(BranchView branch)
    {
        if (_model is null || _desktops is null || _recipeStore is null) return;

        _model.Reconcile(); // capture the live desktops, not any deleted outside Hypertree

        IEnumerable<(string, IReadOnlyList<CapturedApp>)> captured = branch.Desktops.Select(d =>
            (d.Label, (IReadOnlyList<CapturedApp>)SessionCapture.FromWindows(_desktops.WindowsOn(d.Id))));
        Recipe recipe = RecipeBuilder.FromCapture(branch.Name, captured);

        PersistedRecipes lib = _recipeStore.Load();
        lib.Recipes.RemoveAll(x => x.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase));
        lib.Recipes.Add(recipe);
        _recipeStore.Save(lib);

        int steps = recipe.StepCount, desks = recipe.Desktops.Count;
        Notify(steps > 0 ? "Recipe saved" : "Nothing captured",
               steps > 0
                   ? $"“{recipe.Name}” — {steps} app{(steps == 1 ? "" : "s")} across {desks} desktop{(desks == 1 ? "" : "s")}."
                   : $"No open apps found on “{branch.Name}”. Saved an empty recipe you can fill by re-saving later.");
    }

    // Read-only view of a recipe: one row per desktop, its captured apps in the detail line. Pushed over the
    // manager, so Esc — or Enter on any row — pops back to the list.
    private void InspectRecipe(Recipe recipe)
    {
        var items = new List<PaletteItem>();
        foreach (RecipeDesktop d in recipe.Desktops)
        {
            string apps = d.Steps.Count == 0 ? "(no apps)" : string.Join(", ", d.Steps.Select(s => s.Name));
            items.Add(new PaletteItem(d.Label, apps, "▸", () => _stage?.Back()));
        }
        if (items.Count == 0)
            items.Add(new PaletteItem("(empty recipe)", "nothing was captured", null, () => _stage?.Back()));

        _stage?.Present(new PaletteContent($"Recipe · {recipe.Name}",
            "↑↓ move · a desktop → its apps · ↵/Esc back", items));
    }

    private void ConfirmDeleteRecipe(Recipe recipe)
    {
        if (_recipeStore is null) return;
        _stage?.Present(new ConfirmContent($"Delete recipe “{recipe.Name}”?", () =>
        {
            PersistedRecipes lib = _recipeStore.Load();
            lib.Recipes.RemoveAll(x => x.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase));
            _recipeStore.Save(lib);
            ShowSessionsManager(refresh: true); // back to the manager, now without the deleted recipe
        }, confirmLabel: "Delete"));
    }
}
