namespace Hypertree.Spatial;

/// <summary>
/// A cell on the spatial map's integer grid — the unit a room (desktop) is placed at. <see cref="X"/>
/// grows right, <see cref="Y"/> grows down, matching screen space. Positions are relative to an arbitrary
/// origin (the map re-centres on the content), so only <em>relative</em> placement carries meaning; there
/// is no privileged (0,0). Negative coordinates are fine — dragging a room left of everything else is
/// legal and just shifts the framed content.
/// </summary>
public readonly record struct GridPos(int X, int Y)
{
    public GridPos Offset(int dx, int dy) => new(X + dx, Y + dy);
}
