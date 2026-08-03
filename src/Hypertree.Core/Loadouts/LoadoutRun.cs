using System.IO;
using Hypertree.Desktops;

namespace Hypertree.Loadouts;

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
/// A loadout step as it runs: the <see cref="Step"/>, the desktop it targets, its live <see cref="State"/>,
/// the <see cref="Window"/> we matched (0 until found), and an optional <see cref="Note"/> for the
/// AlreadyOpen / Error states. The overlay renders these; the App drives the transitions.
/// </summary>
public sealed class RunStep
{
    public LoadoutStep Step { get; }
    public string DesktopLabel { get; }
    public StepState State { get; set; } = StepState.NotStarted;
    public nint Window { get; set; }
    public string? Note { get; set; }

    public RunStep(LoadoutStep step, string desktopLabel)
    {
        Step = step;
        DesktopLabel = desktopLabel;
    }
}

/// <summary>
/// The OS-free decisions in a loadout restore: flatten a loadout into an ordered run, work out which window a
/// just-launched step produced, and work out which windows are safe to close if the run is aborted. The App
/// owns the timing (launch, poll, settle) and every Win32 call; this is the part worth unit-testing alone.
/// </summary>
public static class LoadoutRun
{
    /// <summary>The run's steps, flattened in desktop-then-step order — the order the executor launches
    /// them, so windows can be attributed to steps one at a time.</summary>
    public static IReadOnlyList<RunStep> Plan(Loadout loadout) =>
        loadout.Desktops.SelectMany(d => d.Steps.Select(s => new RunStep(s, d.Label))).ToList();

    /// <summary>
    /// The window a step's launch produced: a handle in <paramref name="after"/> but not
    /// <paramref name="before"/> (so always a genuinely new window) that we can attribute to the launch, in
    /// descending order of certainty:
    /// <list type="number">
    /// <item>owned by a process in <paramref name="launchedPids"/> (the launched pid plus everything it
    /// spawned) — survives a cold start where the shim we launched re-execs the real app as a child;</item>
    /// <item>its executable matches the step's target by name — a resolved <paramref name="targetName"/> if
    /// the caller has one, else the target's own file-name stem;</item>
    /// <item>it appeared on the throwaway <b>staging</b> desktop (<paramref name="onStaging"/>) — the whole
    /// point of launching there: a genuinely new window on our own scratch desktop is this step's, even when
    /// its process is a pre-existing singleton (VS Code, packaged Windows Terminal) whose pid and exe name we
    /// could never have matched.</item>
    /// </list>
    /// Returns 0 when nothing matches — the launch opened no attributable window (already running elsewhere /
    /// single-instance focus), which the caller records as <see cref="StepState.AlreadyOpen"/>.
    /// </summary>
    public static nint MatchNewWindow(
        LoadoutStep step,
        IReadOnlySet<nint> before,
        IReadOnlyList<WindowInfo> after,
        IReadOnlySet<int> launchedPids,
        string? targetName = null,
        IReadOnlySet<nint>? onStaging = null)
    {
        nint nameMatch = 0, stagingMatch = 0;
        foreach (WindowInfo w in after)
        {
            if (w.Hwnd == 0 || before.Contains(w.Hwnd)) continue;

            // (1) Owned by the process we launched, or one it spawned — the strongest signal, take it now.
            if (w.ProcessId != 0 && launchedPids.Contains(w.ProcessId)) return w.Hwnd;

            // (2) A new window whose exe matches the target by name.
            if (nameMatch == 0 && ExecutableMatches(step.Target, targetName, w.ExecutablePath)) nameMatch = w.Hwnd;

            // (3) A new window on our scratch staging desktop — the fallback for shims and singletons.
            if (stagingMatch == 0 && onStaging is not null && onStaging.Contains(w.Hwnd)) stagingMatch = w.Hwnd;
        }
        return nameMatch != 0 ? nameMatch : stagingMatch;
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

    // Does a new window's executable match the step's target? A name/path fallback, safe because the caller
    // has already restricted us to a genuinely new window. Compares by file-name *stem* (no extension) so a
    // bare command matches its exe — "code" ↔ "Code.exe", "notepad" ↔ "notepad.exe". Prefers a resolved
    // targetName, then whole-path equality (the target IS the exe path), then the target's own stem.
    private static bool ExecutableMatches(string? target, string? targetName, string? windowPath)
    {
        if (string.IsNullOrWhiteSpace(windowPath)) return false;
        string winPath = windowPath.Trim();
        string winStem = Stem(winPath);

        if (!string.IsNullOrWhiteSpace(targetName)
            && string.Equals(winStem, Stem(targetName!.Trim()), StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(target)) return false;
        string t = target!.Trim();

        if (string.Equals(t, winPath, StringComparison.OrdinalIgnoreCase)) return true; // whole-path (original rule)

        string tStem = Stem(t);
        return tStem.Length > 0 && string.Equals(tStem, winStem, StringComparison.OrdinalIgnoreCase);
    }

    // The file name without its extension. Path.* here are pure string ops (no I/O, no throw) even for a URL
    // or an AUMID — they just return the tail, which is the "compare it whole if it isn't a path" behaviour.
    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
}
