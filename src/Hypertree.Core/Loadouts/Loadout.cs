namespace Hypertree.Loadouts;

/// <summary>
/// Where a launched window should end up. v1 records only the target desktop (by label); monitor, window
/// state and exact rect are later phases (docs/design/session-restore.md), added as new optional fields so
/// loadouts written today still run once those land.
/// </summary>
public sealed class Placement
{
    /// <summary>The label of the desktop this window belongs on — matched to a <see cref="LoadoutDesktop"/>
    /// by name. Matching by label (not GUID) is what lets a loadout survive a reboot: the executor recreates
    /// the desktop rather than trying to re-find one that no longer exists.</summary>
    public string Desktop { get; set; } = "";

    /// <summary>The 1-based monitor to place the window on (null = leave it wherever it opens). Captured
    /// from where the window sat, and editable in the review. Exact position/size is a later phase — this
    /// only puts the window on the right screen.</summary>
    public int? Monitor { get; set; }
}

/// <summary>
/// One thing to launch and place: a shell <see cref="Target"/> (exe path, packaged-app AUMID, file, folder
/// or URL) with optional <see cref="Arguments"/> / <see cref="WorkingDirectory"/> — the same contract the
/// launcher already runs — plus where its window should go once it appears.
/// </summary>
public sealed class LoadoutStep
{
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>Display label for the inspector (the captured app's name). Not used to launch — that's
    /// <see cref="Target"/> — but a loadout you can read is the whole point of the loadout model.</summary>
    public string Name { get; set; } = "";

    /// <summary>The window title at capture, kept as a hint when refining the step — e.g. the folder a VS
    /// Code window had open, which tells the user what argument to add. Informational; not launched.</summary>
    public string Hint { get; set; } = "";

    public Placement Placement { get; set; } = new();
}

/// <summary>A desktop in a loadout: a display <see cref="Label"/> and the ordered <see cref="Steps"/> that
/// populate it. The label is both what the user sees and how a step's <see cref="Placement.Desktop"/>
/// finds its home.</summary>
public sealed class LoadoutDesktop
{
    public string Label { get; set; } = "";
    public List<LoadoutStep> Steps { get; set; } = new();
}

/// <summary>How a template variable is filled at apply time — a plain value, or a folder (which the fill
/// prompt can offer a picker for). Persisted as a string; don't rename without a migration.</summary>
public enum VariableKind
{
    Text,
    Folder,
}

/// <summary>
/// Declared metadata for a <c>{name}</c> token used in a loadout's commands: an optional <see cref="Default"/>
/// prefilled at apply time and a <see cref="Kind"/> that lets the fill prompt be smart (a folder picker for
/// <see cref="VariableKind.Folder"/>). Variables are <em>discovered</em> from the command text — this only
/// enriches the ones you want a default or a kind for; a used token with no declaration still gets prompted.
/// </summary>
public sealed class LoadoutVariable
{
    public string Name { get; set; } = "";
    public string? Default { get; set; }
    public VariableKind Kind { get; set; } = VariableKind.Text;
}

/// <summary>
/// A whole-workspace loadout: a named, ordered set of desktops, each with its launch-and-place steps. Keyed
/// by label throughout, so it's reboot-proof, inspectable and portable. Commands may contain <c>{name}</c>
/// tokens filled at apply time (see <see cref="LoadoutVariables"/> / <see cref="LoadoutSubstitution"/>), so one
/// loadout outfits many projects. (docs/design/session-restore.md)
/// </summary>
public sealed class Loadout
{
    public string Name { get; set; } = "";
    public List<LoadoutDesktop> Desktops { get; set; } = new();

    /// <summary>Declared metadata (defaults / kinds) for the loadout's variables. Optional — variables are
    /// discovered from the commands; this only enriches the prompt for the ones listed here.</summary>
    public List<LoadoutVariable> Variables { get; set; } = new();

    /// <summary>Total steps across every desktop — for the inspector's summary line.</summary>
    public int StepCount => Desktops.Sum(d => d.Steps.Count);
}
