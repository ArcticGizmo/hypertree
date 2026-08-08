using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// The id-carrying structural snapshot the spatial scene is built from. This is the spatial twin of
/// <c>NavMap</c>, and it exists <em>because</em> <c>NavMap</c> is deliberately id-free (it carries only what
/// a renderer draws). Spatial state is keyed by ids — group colour by <c>Branch.Id</c>, room position by
/// <c>DesktopId</c> — so the projection needs those ids, which only this snapshot carries.
///
/// Groups are listed in the same top-to-bottom draw order the row model uses (branches above main, main,
/// branches below), which is also the fallback layout order — so before the user places anything, spatial
/// mode looks like the rows.
/// </summary>
public sealed record SpatialSource(IReadOnlyList<SpatialGroupSource> Groups);

/// <summary>One group in the source snapshot: a branch, or the <c>main</c> bucket (<see cref="IsMain"/>,
/// with <see cref="Id"/> = <see cref="Guid.Empty"/>).</summary>
public sealed record SpatialGroupSource(Guid Id, string Name, bool IsMain, IReadOnlyList<SpatialDesktop> Desktops);

/// <summary>One desktop in the source snapshot, carrying its OS id and the same selection/here/count flags
/// the row tiles carry.</summary>
public sealed record SpatialDesktop(DesktopId Id, string Label, bool Selected, bool Here, int WindowCount);
