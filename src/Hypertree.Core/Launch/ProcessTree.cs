namespace Hypertree.Launch;

/// <summary>
/// Reads the live process tree so a loadout restore can tell which windows belong to a launch: the process
/// it started, plus any process that launch spawned — a stub exe that re-execs the real app, an Electron
/// main that forks helpers, a CLI that hands off to a GUI. OS-specific; the Windows head walks a Toolhelp
/// snapshot, and a future head swaps in its own. (docs/design/session-restore.md)
/// </summary>
public interface IProcessTree
{
    /// <summary>The given process id and every process transitively descended from it that is alive right
    /// now. Returns an empty set for an unknown root (<paramref name="rootPid"/> ≤ 0) — the caller then falls
    /// back to matching the window by executable name. Never throws; a snapshot it can't read degrades to
    /// just the root.</summary>
    IReadOnlySet<int> DescendantsAndSelf(int rootPid);
}
