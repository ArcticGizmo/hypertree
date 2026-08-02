using Hypertree.App.Views;
using Hypertree.Recipes;

namespace Hypertree.App;

/// <summary>
/// Recipes — named workspace definitions (desktops × monitors × commands) built by hand in the graphical
/// <see cref="RecipeBuilderContent"/> (docs/design/session-restore.md). The "Recipes…" manager creates, edits
/// and deletes them; applying one to build a branch is <see cref="RestoreRecipe"/> (reached from
/// "Apply recipe…" for now, and from branch creation next). Automatic capture of a running session was
/// removed — manual creation is the model.
/// </summary>
public sealed partial class App
{
    private IRecipeStore? _recipeStore;

    // "Recipes…" — the CRUD manager: build a new recipe, edit or delete a saved one. Mirrors the other
    // managers. refresh:true rebuilds it in place (ReplaceTop) after a builder save or a delete confirm.
    private void ShowRecipesManager(bool refresh)
    {
        if (_recipeStore is null) return;

        var items = new List<PaletteItem>
        {
            new("New recipe…", "build one from scratch", "＋", OpenNewRecipe),
        };
        foreach (Recipe recipe in _recipeStore.Load().Recipes)
        {
            Recipe r = recipe; // capture per iteration
            items.Add(new PaletteItem(r.Name, DescribeRecipe(r), "▤",
                () => OpenRecipeForEdit(r.Name),               // Enter edits it in the builder…
                OnDelete: () => ConfirmDeleteRecipe(r)));       // …Del removes
        }

        var palette = new PaletteContent("Recipes…",
            "↑↓ move · ↵ edit · ⌦ delete · Esc back", items);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    private static string DescribeRecipe(Recipe r)
    {
        int desktops = r.Desktops.Count, steps = r.StepCount;
        return $"{desktops} desktop{(desktops == 1 ? "" : "s")} · {steps} command{(steps == 1 ? "" : "s")}";
    }

    private void OpenNewRecipe()
    {
        // Start with one empty desktop so there's a monitor grid to fill in immediately.
        var recipe = new Recipe { Name = "", Desktops = { new RecipeDesktop { Label = "desktop 1" } } };
        OpenBuilder(recipe, replacingName: null);
    }

    private void OpenRecipeForEdit(string name)
    {
        if (_recipeStore is null) return;
        if (FindRecipe(_recipeStore.Load(), name) is not { } recipe) { _stage?.Back(); return; }
        OpenBuilder(recipe, replacingName: name); // the loaded graph is a fresh copy — Cancel just drops it
    }

    // Present the graphical builder over the manager. On save, upsert into the library (removing the old
    // entry, and the pre-rename name too) and rebuild the manager; Cancel (Back) returns to it untouched.
    private void OpenBuilder(Recipe recipe, string? replacingName)
    {
        if (_recipeStore is null || _desktops is null) return;
        _stage?.Present(new RecipeBuilderContent(recipe, _desktops.MonitorCount, saved =>
        {
            PersistedRecipes lib = _recipeStore.Load();
            lib.Recipes.RemoveAll(x => x.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)
                                    || (replacingName is not null && x.Name.Equals(replacingName, StringComparison.OrdinalIgnoreCase)));
            lib.Recipes.Add(saved);
            _recipeStore.Save(lib);
            ShowRecipesManager(refresh: true); // pop the builder + stale manager, show the updated list
        }));
    }

    private void ConfirmDeleteRecipe(Recipe recipe)
    {
        if (_recipeStore is null) return;
        _stage?.Present(new ConfirmContent($"Delete recipe “{recipe.Name}”?", () =>
        {
            PersistedRecipes lib = _recipeStore.Load();
            lib.Recipes.RemoveAll(x => x.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase));
            _recipeStore.Save(lib);
            ShowRecipesManager(refresh: true);
        }, confirmLabel: "Delete"));
    }

    // Temporary home for "apply a recipe as a new branch" until branch creation absorbs it: a palette of
    // recipes; choosing one confirms then restores. (Restore itself lives in App.Restore.cs.)
    private void ShowApplyRecipe()
    {
        if (_recipeStore is null) return;
        var items = _recipeStore.Load().Recipes.Select(recipe =>
        {
            Recipe r = recipe;
            return new PaletteItem(r.Name, DescribeRecipe(r), "▶", () => ConfirmRestore(r));
        }).ToList();

        if (items.Count == 0)
            items.Add(new PaletteItem("No recipes yet", "build one in “Recipes…” first", null, () => _stage?.Back(),
                                      DisabledReason: "build one in “Recipes…” first"));

        _stage?.Present(new PaletteContent("Apply recipe as a new branch…",
            "↑↓ move · ↵ apply · Esc back", items));
    }

    private static Recipe? FindRecipe(PersistedRecipes lib, string name) =>
        lib.Recipes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
