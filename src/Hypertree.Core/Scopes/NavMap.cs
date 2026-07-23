namespace Hypertree.Scopes;

/// <summary>
/// A render-ready snapshot of the whole map for the overlay/flash. The vertical model (F2): the
/// day-to-day <b>main timeline</b> (<see cref="TopRow"/>) is the pivot, and the fixed group stack is
/// split around it — <see cref="TopPosition"/> groups render <b>above</b> main, the rest below,
/// with the current group sitting directly beneath main. Native Task View is 1-D and can't show this
/// depth axis (PLAN.md §3.4), so this is what makes it legible.
/// </summary>
/// <param name="TopCursor">The remembered main-timeline position — where surfacing returns you.
/// Always valid, even while inside a group, so the board can keep it on the centre column.</param>
/// <param name="OnTop">Whether the user is on the main timeline (vs. inside the current group).</param>
/// <param name="TopPosition">The main timeline's fixed slot: how many groups render above it.
/// <c>Groups[0..TopPosition-1]</c> stack above main, then main, then <c>Groups[TopPosition..]</c>
/// below. Fixed as the cursor navigates (stable pivot) — main does not move.</param>
public sealed record NavMap(
    IReadOnlyList<NavMapTile> TopRow,
    int TopCursor,
    bool OnTop,
    IReadOnlyList<NavMapGroup> Groups,
    int TopPosition = 0);

/// <summary>One desktop tile.</summary>
/// <param name="Label">Display label.</param>
/// <param name="IsCurrent">Whether this is the desktop the user is on right now.</param>
public sealed record NavMapTile(string Label, bool IsCurrent);

/// <summary>One group in the fixed stack, in listed order (index 0 first). Its position relative to
/// the main timeline is given by <see cref="NavMap.TopPosition"/>, not by reordering.</summary>
/// <param name="Index">The group's stable index (for click-to-navigate / remove — equals its list position).</param>
/// <param name="Name">The group's name.</param>
/// <param name="Desktops">The group's desktops; one is current only when this is the level you're on.</param>
/// <param name="IsCurrentLevel">Whether the user is currently inside this group.</param>
/// <param name="Cursor">The group's remembered position (resume point) — kept on the centre column so
/// returning to this group lands centred.</param>
public sealed record NavMapGroup(int Index, string Name, IReadOnlyList<NavMapTile> Desktops, bool IsCurrentLevel, int Cursor);
