using Hypertree.App.Views;
using Hypertree.Launch;
using Hypertree.Recipes;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// Sessions as <b>recipes</b> (docs/design/session-restore.md): capture what's open on a branch's desktops
/// into a named recipe — an ordered set of desktops, each with its launch-and-place steps — then restore it
/// (see <see cref="RestoreRecipe"/>). The "Sessions…" manager saves the current branch, opens a recipe to
/// restore it or edit its steps (a step's target / arguments / working directory — so a captured VS Code or
/// terminal can be given the folder it should open, which the capture can't infer), or deletes a recipe.
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
                () => OpenRecipe(r.Name, replaceTop: 0),      // Enter opens it (restore / edit steps)…
                OnDelete: () => ConfirmDeleteRecipe(r)));      // …Del removes
        }

        var palette = new PaletteContent("Sessions…",
            "↑↓ move · ↵ open · ⌦ delete · Esc back", items);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    // The recipe detail hub: restore the recipe, or edit / remove its individual steps. Editing a step is how
    // a captured VS Code or terminal gets the folder it should open (as an argument or working directory) —
    // the capture only knows the executable, so a bare relaunch would open a blank window.
    // replaceTop 0 pushes it fresh over the manager; >0 rebuilds it in place after an edit/remove (ReplaceTop).
    private void OpenRecipe(string recipeName, int replaceTop)
    {
        if (_recipeStore is null) return;
        Recipe? recipe = FindRecipe(_recipeStore.Load(), recipeName);
        if (recipe is null) { _stage?.Back(); return; } // deleted from under us

        var items = new List<PaletteItem>
        {
            new("▶  Restore this recipe", DescribeRecipe(recipe), null, () => ConfirmRestore(recipe)),
        };
        for (int di = 0; di < recipe.Desktops.Count; di++)
        {
            RecipeDesktop d = recipe.Desktops[di];
            for (int si = 0; si < d.Steps.Count; si++)
            {
                int dj = di, sj = si; // capture per iteration
                RecipeStep s = d.Steps[si];
                items.Add(new PaletteItem($"{d.Label} · {s.Name}", StepSummary(s), "✎",
                    () => EditStep(recipeName, dj, sj),               // Enter edits target / args / working dir…
                    OnDelete: () => RemoveStep(recipeName, dj, sj)));  // …Del removes the step
            }
        }

        var palette = new PaletteContent($"Recipe · {recipe.Name}",
            "↑↓ move · ↵ restore / edit step · ⌦ remove step · Esc back", items);
        if (replaceTop > 0) _stage?.ReplaceTop(replaceTop, palette); else _stage?.Present(palette);
    }

    // A step's one-line detail: what it launches, with its arguments, working directory and monitor when set.
    private static string StepSummary(RecipeStep s)
    {
        string txt = s.Target;
        if (!string.IsNullOrWhiteSpace(s.Arguments)) txt += " " + s.Arguments;
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.WorkingDirectory)) tags.Add("in " + s.WorkingDirectory);
        if (s.Placement.Monitor is int m) tags.Add($"monitor {m}");
        if (tags.Count > 0) txt += "  ·  " + string.Join("  ·  ", tags);
        return txt;
    }

    // Refine a step: its target, arguments, working directory and monitor. On save, write them back into
    // the stored recipe and rebuild the hub.
    private void EditStep(string recipeName, int di, int si)
    {
        if (_recipeStore is null || StepAt(_recipeStore.Load(), recipeName, di, si) is not { } s) return;

        _stage?.Present(new RecipeStepContent(edit =>
        {
            PersistedRecipes lib = _recipeStore.Load();
            if (StepAt(lib, recipeName, di, si) is { } step)
            {
                step.Name = edit.Name;
                step.Target = edit.Target;
                step.Arguments = edit.Arguments;
                step.WorkingDirectory = edit.WorkingDirectory;
                step.Placement.Monitor = edit.Monitor;
                _recipeStore.Save(lib);
            }
            OpenRecipe(recipeName, replaceTop: 2); // pop the editor + stale hub, show the updated one
        }, s));
    }

    // Remove a step (Del on its row); an emptied desktop drops out of the recipe. Rebuilds the hub in place.
    private void RemoveStep(string recipeName, int di, int si)
    {
        if (_recipeStore is null) return;
        PersistedRecipes lib = _recipeStore.Load();
        if (FindRecipe(lib, recipeName) is { } recipe && di < recipe.Desktops.Count && si < recipe.Desktops[di].Steps.Count)
        {
            recipe.Desktops[di].Steps.RemoveAt(si);
            if (recipe.Desktops[di].Steps.Count == 0) recipe.Desktops.RemoveAt(di);
            _recipeStore.Save(lib);
        }
        OpenRecipe(recipeName, replaceTop: 1);
    }

    private static Recipe? FindRecipe(PersistedRecipes lib, string name) =>
        lib.Recipes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static RecipeStep? StepAt(PersistedRecipes lib, string recipeName, int di, int si)
    {
        if (FindRecipe(lib, recipeName) is not { } r || di < 0 || di >= r.Desktops.Count) return null;
        RecipeDesktop d = r.Desktops[di];
        return si >= 0 && si < d.Steps.Count ? d.Steps[si] : null;
    }

    private static string DescribeRecipe(Recipe r)
    {
        int desktops = r.Desktops.Count, steps = r.StepCount;
        return $"{desktops} desktop{(desktops == 1 ? "" : "s")} · {steps} app{(steps == 1 ? "" : "s")}";
    }

    // Snapshot the branch's live desktops into a draft recipe (overwriting any recipe of the same name),
    // then open it as a prefilled review — a list of suggested launch steps to refine into real commands
    // (add VS Code's folder, a terminal's working directory, fix a monitor) or trim. Nothing is placed
    // yet; this is just capture + curate.
    private void SaveBranchAsRecipe(BranchView branch)
    {
        if (_model is null || _desktops is null || _recipeStore is null) return;

        _model.Reconcile(); // capture the live desktops, not any deleted outside Hypertree

        IEnumerable<(string, IReadOnlyList<CapturedApp>)> captured = branch.Desktops.Select(d =>
            (d.Label, (IReadOnlyList<CapturedApp>)SessionCapture.FromWindows(_desktops.WindowsOn(d.Id))));
        Recipe recipe = RecipeBuilder.FromCapture(branch.Name, captured);

        if (recipe.StepCount == 0)
        {
            Notify("Nothing captured", $"No open apps found on “{branch.Name}” to build a recipe from.");
            return;
        }

        PersistedRecipes lib = _recipeStore.Load();
        lib.Recipes.RemoveAll(x => x.Name.Equals(recipe.Name, StringComparison.OrdinalIgnoreCase));
        lib.Recipes.Add(recipe);
        _recipeStore.Save(lib);

        OpenRecipe(recipe.Name, replaceTop: 0); // straight into the review, over the manager
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
