using Hypertree.Launch;

namespace Hypertree.Recipes;

/// <summary>
/// The OS-free generator turning a branch capture into a <see cref="Recipe"/>: one desktop per captured
/// desktop (in order), one step per app, each placed on its own desktop's label. Kept pure so the recipe's
/// shape is unit-testable without a live branch — the App supplies the per-desktop app lists from
/// <see cref="SessionCapture"/>.
/// </summary>
public static class RecipeBuilder
{
    /// <summary>
    /// Build a recipe named <paramref name="name"/> from <paramref name="desktops"/> (each a label and the
    /// apps captured on it, in branch order). Desktops with no apps are dropped — a recipe records work, not
    /// empty rooms — so a branch where nothing was open yields a recipe with no desktops.
    /// </summary>
    public static Recipe FromCapture(string name,
        IEnumerable<(string Label, IReadOnlyList<CapturedApp> Apps)> desktops)
    {
        var recipe = new Recipe { Name = name };
        foreach ((string label, IReadOnlyList<CapturedApp> apps) in desktops)
        {
            if (apps.Count == 0) continue;
            var desktop = new RecipeDesktop { Label = label };
            foreach (CapturedApp app in apps)
                desktop.Steps.Add(new RecipeStep
                {
                    Target = app.Path,
                    Name = app.Name,
                    Placement = new Placement { Desktop = label },
                });
            recipe.Desktops.Add(desktop);
        }
        return recipe;
    }
}
