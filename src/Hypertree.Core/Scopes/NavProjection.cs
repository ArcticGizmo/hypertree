using Hypertree.Desktops;
using Hypertree.Spatial;
using Hypertree.Status;

namespace Hypertree.Scopes;

/// <summary>
/// An immutable read-only view of the navigation layout — the top row, the branches (read-only here), the
/// cursor, and the precomputed draw order / cursor row — enough for <see cref="NavProjection"/> to build the
/// render and status DTOs without touching <see cref="NavigationModel"/>'s mutable state. Built and discarded
/// per projection call (a summon-time operation, not a per-keystroke one).
/// </summary>
internal sealed record NavLayout(
    IReadOnlyList<DesktopRef> TopRow,
    IReadOnlyList<Branch> Branches,
    bool OnMain,
    int CurrentBranch,
    int TopIndex,
    int MainSlot,                 // already clamped to 0..Branches.Count
    IReadOnlyList<int> RowOrder,  // branch indices in draw order, with NavigationModel.MainRowMarker for main
    int CurrentRow);              // the cursor's index within RowOrder

/// <summary>
/// Turns a <see cref="NavLayout"/> into the view/publish DTOs — the row map, the spatial source, and the
/// status snapshot. Pure projection: no state, no side effects, no OS calls, so it's directly unit-testable
/// from a hand-built layout. Split out of <see cref="NavigationModel"/> so the model owns the navigation
/// ladder and structure while the "shape it for a consumer" logic lives here.
/// </summary>
internal static class NavProjection
{
    /// <summary>The id-carrying structural snapshot the spatial map is built from. Keeps the branch/desktop
    /// ids spatial state is keyed by, and emits groups in draw order (branches above main, main as the
    /// <see cref="Guid.Empty"/> bucket, branches below).</summary>
    public static SpatialSource Spatial(NavLayout m, Func<DesktopId, int> windows, DesktopId? cameFrom)
    {
        bool CameFrom(DesktopId id) => cameFrom == id;

        SpatialGroupSource MainGroup() => new(Guid.Empty, "main", IsMain: true,
            m.TopRow.Select((d, i) => new SpatialDesktop(
                d.Id, d.Label, m.OnMain && i == m.TopIndex, CameFrom(d.Id), windows(d.Id))).ToList());

        SpatialGroupSource BranchGroup(int gi)
        {
            Branch g = m.Branches[gi];
            bool current = !m.OnMain && gi == m.CurrentBranch;
            var desks = new List<SpatialDesktop>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                DesktopRef d = g.Desktops[j];
                desks.Add(new SpatialDesktop(d.Id, d.Label, current && j == g.LastUsedIndex,
                                             CameFrom(d.Id), windows(d.Id)));
            }
            return new SpatialGroupSource(g.Id, g.Name, IsMain: false, desks);
        }

        var groups = new List<SpatialGroupSource>(m.Branches.Count + 1);
        foreach (int r in m.RowOrder) groups.Add(r == NavigationModel.MainRowMarker ? MainGroup() : BranchGroup(r));
        return new SpatialSource(groups);
    }

    /// <summary>The stack as published to the outside world (the CLI, anything watching the status file):
    /// rows top-to-bottom with main in its slot, plus where the cursor is. Carries no window counts — nothing
    /// downstream of the status file wants them, and this runs on every navigation.</summary>
    public static StatusSnapshot Status(NavLayout m)
    {
        StatusRow MainRow() => new()
        {
            Kind = RowKind.Main,
            Name = RowKind.Main,
            Cursor = m.TopRow.Count == 0 ? 0 : Math.Clamp(m.TopIndex, 0, m.TopRow.Count - 1),
            Desktops = m.TopRow.Select(d => new StatusDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        };

        StatusRow BranchRow(Branch g) => new()
        {
            Kind = RowKind.Branch,
            Id = g.Id,
            Name = g.Name,
            Cursor = g.LastUsedIndex,
            Desktops = g.Desktops.Select(d => new StatusDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        };

        var rows = new List<StatusRow>(m.Branches.Count + 1);
        foreach (int r in m.RowOrder)
            rows.Add(r == NavigationModel.MainRowMarker ? MainRow() : BranchRow(m.Branches[r]));

        return new StatusSnapshot
        {
            Rows = rows,
            Current = new StatusPosition
            {
                Row = m.CurrentRow,
                Desktop = rows.Count > m.CurrentRow ? rows[m.CurrentRow].Cursor : 0,
            },
        };
    }
}
