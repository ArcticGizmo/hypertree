namespace Hypertree.Scopes;

/// <summary>
/// A render-ready snapshot of the whole 2-D map for the HUD overlay: the day-to-day anchor row plus
/// the scope hanging off the current anchor, with "you are here" marked. Native Task View is 1-D and
/// can't show the depth axis (PLAN.md §3.4), so this is what makes the second axis legible.
/// </summary>
public sealed record NavMap(
    IReadOnlyList<NavMapAnchor> Anchors,
    bool InScope,
    string? ScopeName,
    IReadOnlyList<NavMapDesktop>? ScopeDesktops);

/// <summary>One anchor on the day-to-day row.</summary>
/// <param name="Label">Display label (e.g. "Main").</param>
/// <param name="HasScope">Whether a scope hangs beneath it (a dive target).</param>
/// <param name="IsCurrentColumn">Whether the user is on this anchor's column (dived or not).</param>
public sealed record NavMapAnchor(string Label, bool HasScope, bool IsCurrentColumn);

/// <summary>One desktop within the current anchor's scope.</summary>
/// <param name="Label">Display label (e.g. "API").</param>
/// <param name="IsCurrent">Whether this is the desktop the user is on right now (only when dived).</param>
public sealed record NavMapDesktop(string Label, bool IsCurrent);

/// <summary>
/// Full-topology snapshot for the interactive map/config overlay (every anchor and its scope, not
/// just the current one). Distinct from <see cref="NavMap"/>, which is the current-focused flash.
/// </summary>
/// <param name="Index">The anchor's index (stable handle for add/remove operations).</param>
/// <param name="AnchorLabel">Display label of the day-to-day desktop.</param>
/// <param name="IsCurrentColumn">Whether the user is currently on this column.</param>
/// <param name="ScopeName">The scope's name, or null if the anchor has no scope.</param>
/// <param name="ScopeDesktops">The scope's desktop labels in order (empty if no scope).</param>
public sealed record StreamInfo(
    int Index,
    string AnchorLabel,
    bool IsCurrentColumn,
    string? ScopeName,
    IReadOnlyList<string> ScopeDesktops);
