namespace Hypertree.Recipes;

/// <summary>
/// Where a launched window should end up. v1 records only the target desktop (by label); monitor, window
/// state and exact rect are later phases (docs/design/session-restore.md), added as new optional fields so
/// recipes written today still run once those land.
/// </summary>
public sealed class Placement
{
    /// <summary>The label of the desktop this window belongs on — matched to a <see cref="RecipeDesktop"/>
    /// by name. Matching by label (not GUID) is what lets a recipe survive a reboot: the executor recreates
    /// the desktop rather than trying to re-find one that no longer exists.</summary>
    public string Desktop { get; set; } = "";
}

/// <summary>
/// One thing to launch and place: a shell <see cref="Target"/> (exe path, packaged-app AUMID, file, folder
/// or URL) with optional <see cref="Arguments"/> / <see cref="WorkingDirectory"/> — the same contract the
/// launcher already runs — plus where its window should go once it appears.
/// </summary>
public sealed class RecipeStep
{
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>Display label for the inspector (the captured app's name). Not used to launch — that's
    /// <see cref="Target"/> — but a recipe you can read is the whole point of the recipe model.</summary>
    public string Name { get; set; } = "";

    public Placement Placement { get; set; } = new();
}

/// <summary>A desktop in a recipe: a display <see cref="Label"/> and the ordered <see cref="Steps"/> that
/// populate it. The label is both what the user sees and how a step's <see cref="Placement.Desktop"/>
/// finds its home.</summary>
public sealed class RecipeDesktop
{
    public string Label { get; set; } = "";
    public List<RecipeStep> Steps { get; set; } = new();
}

/// <summary>
/// A whole-workspace recipe: a named, ordered set of desktops, each with its launch-and-place steps. Keyed
/// by label throughout, so it's reboot-proof, inspectable and portable. Generated from a branch capture
/// today; hand-authored as a template later — the same executor runs both (docs/design/session-restore.md).
/// </summary>
public sealed class Recipe
{
    public string Name { get; set; } = "";
    public List<RecipeDesktop> Desktops { get; set; } = new();

    /// <summary>Total steps across every desktop — for the inspector's summary line.</summary>
    public int StepCount => Desktops.Sum(d => d.Steps.Count);
}
