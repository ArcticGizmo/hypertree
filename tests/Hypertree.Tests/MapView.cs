using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Spatial;
using Hypertree.Status;

namespace Hypertree.Tests;

// Test-only view of the navigation layout in the shape the old (removed) NavMap carried, rebuilt from the
// surviving projections. It lets the navigation/rearrange/persistence tests keep observing the model through
// a "top row + stacked branches + main slot" lens — exactly the facts those tests care about — without the
// production code carrying a linear render model any more.
//
// The mapping is one-to-one: both the spatial source and the status snapshot list their rows in the stack's
// draw order (branches above main, main as the Guid.Empty "main" bucket, branches below), so the index of
// the main group IS the main slot, the non-main groups in that order are the branch stack in natural order,
// and the status snapshot (zipped by that same index) supplies each branch's resume cursor.
internal sealed record MapTile(string Label, bool IsCurrent, bool IsHere, int WindowCount);

internal sealed record MapBranch(string Name, IReadOnlyList<MapTile> Desktops, int Cursor);

internal sealed record MapView(IReadOnlyList<MapTile> TopRow, bool OnTop, int TopPosition,
                               IReadOnlyList<MapBranch> Branches);

internal static class MapViews
{
    public static MapView Map(this NavigationModel m, DesktopId? cameFrom = null)
    {
        IReadOnlyList<SpatialGroupSource> groups = m.BuildSpatialSource(cameFrom).Groups;
        IReadOnlyList<StatusRow> rows = m.BuildStatus().Rows; // same draw order; carries each row's resume cursor

        int slot = 0;
        for (int i = 0; i < groups.Count; i++) if (groups[i].IsMain) { slot = i; break; }

        SpatialGroupSource main = groups.First(g => g.IsMain);
        var top = main.Desktops.Select(Tile).ToList();
        bool onTop = main.Desktops.Any(d => d.Selected);

        var branches = new List<MapBranch>();
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].IsMain) continue;
            int cursor = i < rows.Count ? rows[i].Cursor : 0;
            branches.Add(new MapBranch(groups[i].Name, groups[i].Desktops.Select(Tile).ToList(), cursor));
        }

        return new MapView(top, onTop, slot, branches);
    }

    private static MapTile Tile(SpatialDesktop d) => new(d.Label, d.Selected, d.Here, d.WindowCount);
}
