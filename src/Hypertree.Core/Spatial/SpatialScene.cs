using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>One room in the spatial scene: a desktop placed on the grid, tagged with the group it belongs
/// to. <see cref="Selected"/> is the blue focus/target; <see cref="Here"/> the green "you are here".</summary>
public sealed record SpatialRoom(
    DesktopId Id, string Label, GridPos Pos, Guid GroupId, bool IsMainGroup,
    bool Selected, bool Here, int WindowCount);

/// <summary>One logical group in the spatial scene — the evolution of a branch (<see cref="IsMain"/> marks
/// the <c>main</c> "ungrouped" bucket). <see cref="Color"/> is fully resolved (explicit choice, else the
/// palette default). <see cref="Members"/> lists its rooms for hull/fragment computation downstream.</summary>
public sealed record SpatialGroup(
    Guid Id, string Name, string Color, bool IsMain, IReadOnlyList<DesktopId> Members);

/// <summary>
/// The theme-agnostic, normalised spatial view — the spatial twin of <c>Scene</c>. It merges the structural
/// <see cref="SpatialSource"/> (ids, labels, selection) with the persisted <see cref="SpatialState"/>
/// (explicit colours and positions) into placed <see cref="Rooms"/> and coloured <see cref="Groups"/>, one
/// pass, so the layout/painter downstream is pure geometry.
///
/// Both spatial facts are <b>sparse with a sensible default</b>:
/// <list type="bullet">
///   <item>an unplaced desktop falls back to a <b>row layout</b> position — group <c>gi</c> forms row
///   <c>gi</c> (in the source's draw order) and its desktop <c>di</c> sits at column <c>di</c> — so a
///   never-arranged map simply mirrors the rows;</item>
///   <item>an un-recoloured group falls back to a <b>stable palette hue</b> derived from its id (main is
///   neutral).</item>
/// </list>
/// Anything the user has explicitly set wins over the default.
/// </summary>
public sealed record SpatialScene(IReadOnlyList<SpatialGroup> Groups, IReadOnlyList<SpatialRoom> Rooms)
{
    public static SpatialScene From(SpatialSource source, SpatialState state)
    {
        var groups = new List<SpatialGroup>(source.Groups.Count);
        var rooms = new List<SpatialRoom>();

        for (int gi = 0; gi < source.Groups.Count; gi++)
        {
            SpatialGroupSource g = source.Groups[gi];
            // main is neutral and never takes a palette hue or an explicit colour; a branch uses its
            // explicit colour if set, else a stable default derived from its id.
            string color = g.IsMain ? SpatialPalette.Main : state.Color(g.Id) ?? SpatialPalette.For(g.Id);

            var members = new List<DesktopId>(g.Desktops.Count);
            for (int di = 0; di < g.Desktops.Count; di++)
            {
                SpatialDesktop d = g.Desktops[di];
                GridPos pos = state.Position(d.Id.Value) ?? new GridPos(di, gi); // stored wins; else row layout
                rooms.Add(new SpatialRoom(d.Id, d.Label, pos, g.Id, g.IsMain,
                                          d.Selected, d.Here, d.WindowCount));
                members.Add(d.Id);
            }

            groups.Add(new SpatialGroup(g.Id, g.Name, color, g.IsMain, members));
        }

        return new SpatialScene(groups, rooms);
    }
}
