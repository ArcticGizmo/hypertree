using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// A desktop as it sits in Hypertree's 2-D map: an OS desktop id plus the human label shown in
/// the HUD (e.g. "SPA", "API"). Windows only has a flat desktop list; Hypertree overlays the
/// second (depth) axis by grouping these into <see cref="Anchor"/>s and <see cref="Scope"/>s.
/// </summary>
public sealed record DesktopRef(DesktopId Id, string Label);

/// <summary>
/// A scope (one worktree's stream of desktops) hanging beneath a single anchor. The ordered
/// <see cref="Desktops"/> are the horizontal strip you move along *inside* the scope;
/// <see cref="LastUsedIndex"/> is the resume point — re-diving lands here, not at the start
/// (PLAN.md §3, sub-decision 3).
/// </summary>
public sealed class Scope
{
    public string Name { get; }
    public IReadOnlyList<DesktopRef> Desktops { get; }

    /// <summary>Index within <see cref="Desktops"/> last occupied inside this scope. Always valid.</summary>
    public int LastUsedIndex { get; set; }

    public Scope(string name, IReadOnlyList<DesktopRef> desktops, int lastUsedIndex = 0)
    {
        if (desktops.Count == 0) throw new ArgumentException("A scope needs at least one desktop.", nameof(desktops));
        Name = name;
        Desktops = desktops;
        LastUsedIndex = Math.Clamp(lastUsedIndex, 0, desktops.Count - 1);
    }
}

/// <summary>
/// One day-to-day desktop on the top (horizontal) row, optionally with a <see cref="Scope"/>
/// hanging beneath it. The anchor is the scope's spatial memory and its surface target: diving
/// enters the scope, surfacing always returns here (PLAN.md §3, sub-decisions 1 &amp; 2).
/// </summary>
public sealed class Anchor
{
    public DesktopRef Desktop { get; }

    /// <summary>
    /// The scope hanging beneath this anchor, or null. Mutable so a scope can be defined/removed at
    /// runtime (the navigation model attaches it via <see cref="NavigationModel.DefineScopeHere"/>).
    /// </summary>
    public Scope? Scope { get; set; }

    public Anchor(DesktopRef desktop, Scope? scope = null)
    {
        Desktop = desktop;
        Scope = scope;
    }
}

/// <summary>
/// The whole map: the ordered day-to-day row of anchors. This is the shape the navigation model
/// walks. In M1 it's hard-coded; from M2 it's derived from git worktrees + the scope store.
/// </summary>
public sealed class Topology
{
    public IReadOnlyList<Anchor> Anchors { get; }

    public Topology(IReadOnlyList<Anchor> anchors)
    {
        if (anchors.Count == 0) throw new ArgumentException("Topology needs at least one anchor.", nameof(anchors));
        Anchors = anchors;
    }
}
