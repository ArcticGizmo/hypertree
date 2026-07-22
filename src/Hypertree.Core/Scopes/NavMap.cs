namespace Hypertree.Scopes;

/// <summary>
/// A render-ready snapshot of the whole map for the overlay/flash: the day-to-day top row of
/// ungrouped desktops, plus the groups in carousel display order (nearest/active first). Native Task
/// View is 1-D and can't show this depth axis (PLAN.md §3.4), so this is what makes it legible.
/// </summary>
public sealed record NavMap(
    IReadOnlyList<NavMapTile> TopRow,
    bool OnTop,
    IReadOnlyList<NavMapGroup> Groups);

/// <summary>One desktop tile.</summary>
/// <param name="Label">Display label.</param>
/// <param name="IsCurrent">Whether this is the desktop the user is on right now.</param>
public sealed record NavMapTile(string Label, bool IsCurrent);

/// <summary>One group in the stack, in carousel display order (index 0 = nearest = active).</summary>
/// <param name="Index">The group's stable index (for click-to-navigate / remove — NOT the display position).</param>
/// <param name="Name">The group's name.</param>
/// <param name="Desktops">The group's desktops; one is current only when this is the level you're on.</param>
/// <param name="IsCurrentLevel">Whether the user is currently inside this group.</param>
public sealed record NavMapGroup(int Index, string Name, IReadOnlyList<NavMapTile> Desktops, bool IsCurrentLevel);
