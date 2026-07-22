using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// A desktop as it appears in the map: an OS desktop id plus the label shown on its tile.
/// </summary>
public sealed record DesktopRef(DesktopId Id, string Label);

/// <summary>
/// A group (one worktree's stream of desktops) — a horizontal timeline that hangs below the
/// day-to-day top row. Groups form a wrapping carousel: the active group is drawn nearest the top
/// row and is the one <c>Down</c> dives into; the others stack below it and rotate. Diving into a
/// group resumes its <see cref="LastUsedIndex"/> rather than restarting at the first desktop.
/// </summary>
public sealed class Group
{
    public string Name { get; }
    public IReadOnlyList<DesktopRef> Desktops { get; }

    /// <summary>Index within <see cref="Desktops"/> last occupied — the resume point. Always valid.</summary>
    public int LastUsedIndex { get; set; }

    public Group(string name, IReadOnlyList<DesktopRef> desktops, int lastUsedIndex = 0)
    {
        if (desktops.Count == 0) throw new ArgumentException("A group needs at least one desktop.", nameof(desktops));
        Name = name;
        Desktops = desktops;
        LastUsedIndex = Math.Clamp(lastUsedIndex, 0, desktops.Count - 1);
    }
}
