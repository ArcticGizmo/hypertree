using Hypertree.Desktops;

namespace Hypertree.Recipes;

/// <summary>Where a step is in the restore pipeline (docs/design/session-restore.md).</summary>
public enum StepState
{
    /// <summary>Queued; nothing launched yet.</summary>
    NotStarted,

    /// <summary>Launched on the staging desktop, waiting for its window to appear.</summary>
    Creating,

    /// <summary>Window found; being moved to its target desktop.</summary>
    Placing,

    /// <summary>Window placed on its target desktop.</summary>
    Done,

    /// <summary>No new window appeared — the app was already running and focused an existing window
    /// (single-instance), so there was nothing to place. Info, not failure.</summary>
    AlreadyOpen,

    /// <summary>The launch failed, or another problem stopped the step — see <see cref="RunStep.Note"/>.</summary>
    Error,
}

/// <summary>
/// A recipe step as it runs: the <see cref="Step"/>, the desktop it targets, its live <see cref="State"/>,
/// the <see cref="Window"/> we matched (0 until found), and an optional <see cref="Note"/> for the
/// AlreadyOpen / Error states. The overlay renders these; the App drives the transitions.
/// </summary>
public sealed class RunStep
{
    public RecipeStep Step { get; }
    public string DesktopLabel { get; }
    public StepState State { get; set; } = StepState.NotStarted;
    public nint Window { get; set; }
    public string? Note { get; set; }

    public RunStep(RecipeStep step, string desktopLabel)
    {
        Step = step;
        DesktopLabel = desktopLabel;
    }
}

/// <summary>
/// The OS-free decisions in a recipe restore: flatten a recipe into an ordered run, work out which window a
/// just-launched step produced, and work out which windows are safe to close if the run is aborted. The App
/// owns the timing (launch, poll, settle) and every Win32 call; this is the part worth unit-testing alone.
/// </summary>
public static class RecipeRun
{
    /// <summary>The run's steps, flattened in desktop-then-step order — the order the executor launches
    /// them, so windows can be attributed to steps one at a time.</summary>
    public static IReadOnlyList<RunStep> Plan(Recipe recipe) =>
        recipe.Desktops.SelectMany(d => d.Steps.Select(s => new RunStep(s, d.Label))).ToList();

    /// <summary>
    /// The window a step's launch produced: a handle in <paramref name="after"/> but not
    /// <paramref name="before"/> whose process executable path matches the step's target. Returns 0 when
    /// none — the launch opened no new window (already running / single-instance), which the caller records
    /// as <see cref="StepState.AlreadyOpen"/>. Path match is case-insensitive; blank paths are ignored.
    /// </summary>
    public static nint MatchNewWindow(RecipeStep step, IReadOnlySet<nint> before, IReadOnlyList<WindowInfo> after)
    {
        foreach (WindowInfo w in after)
        {
            if (w.Hwnd == 0 || before.Contains(w.Hwnd)) continue;
            if (string.IsNullOrWhiteSpace(w.ExecutablePath)) continue;
            if (PathMatches(step.Target, w.ExecutablePath)) return w.Hwnd;
        }
        return 0;
    }

    /// <summary>
    /// The windows to close on an abort: those a step matched (so we know we launched them) that never
    /// reached <see cref="StepState.Done"/> — still sitting on staging rather than placed on a target. The
    /// App additionally confirms each is on the staging desktop (the certainty rule) before closing, so a
    /// window already moved home is never at risk.
    /// </summary>
    public static IReadOnlyList<nint> CleanupCandidates(IEnumerable<RunStep> steps) =>
        steps.Where(s => s.Window != 0 && s.State != StepState.Done)
             .Select(s => s.Window)
             .ToList();

    // Full-path equality, case-insensitive — safer than a file-name match, which would conflate two
    // different installs of the same exe.
    private static bool PathMatches(string? target, string? windowPath) =>
        string.Equals(target?.Trim(), windowPath?.Trim(), StringComparison.OrdinalIgnoreCase);
}
