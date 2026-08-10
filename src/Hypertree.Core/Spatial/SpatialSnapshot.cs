using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Store;

namespace Hypertree.Spatial;

/// <summary>
/// The spatial half of a saved <see cref="Snapshot"/>: capturing the live room positions / group colours
/// into a snapshot, previewing a snapshot as a <see cref="SpatialScene"/>, and re-applying a restored
/// snapshot's spatial facts onto the live <see cref="SpatialState"/>.
///
/// It lives beside the spatial model (not on <see cref="Snapshot"/>) because it is the one place that knows
/// both sides — the row-shaped snapshot and the id-keyed spatial state — and because a restore has to
/// <b>re-key</b>: a restore mints fresh desktop GUIDs (for desktops it re-creates) and fresh branch ids (a
/// snapshot is a template), so the stored positions/colours must be moved onto whatever ids the restore
/// actually produced. Everything here is pure and unit-tested; the app supplies the two id remaps.
/// </summary>
public static class SpatialSnapshot
{
    /// <summary>Record the spatial facts for the ids <paramref name="snap"/> names — group colours by branch
    /// id, room positions by desktop id — reading the user's explicit choices out of <paramref name="state"/>.
    /// Only explicit values are written (both tables are sparse), so a never-arranged map captures nothing and
    /// restores to defaults.</summary>
    public static void Capture(Snapshot snap, SpatialState state)
    {
        snap.GroupColors.Clear();
        snap.Positions.Clear();

        foreach (PersistedBranch b in snap.Branches)
            if (state.Color(b.Id) is { } hex)
                snap.GroupColors[b.Id.ToString()] = hex;

        foreach (PersistedDesktop d in AllDesktops(snap))
            if (state.Position(d.Id) is { } pos)
                snap.Positions[d.Id.ToString()] = pos;
    }

    /// <summary>The id-carrying structural source for a snapshot, in the same top-to-bottom draw order the
    /// live map uses — branches above main, main (the <see cref="Guid.Empty"/> "ungrouped" bucket), branches
    /// below — so a preview built from it reads as the saved arrangement. Selection/here/count flags are all
    /// neutral: a snapshot is a target layout, not the live cursor.</summary>
    public static SpatialSource SourceFrom(Snapshot snap)
    {
        SpatialGroupSource Main() => new(Guid.Empty, "main", IsMain: true,
            snap.MainDesktops.Select(Desk).ToList());
        SpatialGroupSource Branch(PersistedBranch pg) => new(pg.Id, pg.Name, IsMain: false,
            pg.Desktops.Select(Desk).ToList());

        var groups = new List<SpatialGroupSource>(snap.Branches.Count + 1);
        foreach (int i in RowSplice.Order(snap.Branches.Count, snap.MainSlot))
            groups.Add(i == RowSplice.MainMarker ? Main() : Branch(snap.Branches[i]));
        return new SpatialSource(groups);

        static SpatialDesktop Desk(PersistedDesktop d) =>
            new(new DesktopId(d.Id), d.Label, Selected: false, Here: false, WindowCount: 0);
    }

    /// <summary>A <see cref="SpatialState"/> carrying the snapshot's stored colours/positions verbatim (the
    /// keys are the captured ids, which match <see cref="SourceFrom"/>'s ids) — so
    /// <c>SpatialScene.From(SourceFrom(snap), StateFrom(snap))</c> previews the saved map.</summary>
    public static SpatialState StateFrom(Snapshot snap) => new()
    {
        GroupColors = new Dictionary<string, string>(snap.GroupColors),
        Positions = new Dictionary<string, GridPos>(snap.Positions),
    };

    /// <summary>Convenience: the previewed scene for a snapshot (source + stored state, merged).</summary>
    public static SpatialScene SceneFrom(Snapshot snap) => SpatialScene.From(SourceFrom(snap), StateFrom(snap));

    /// <summary>
    /// Write the snapshot's spatial facts onto the live <paramref name="target"/> state, re-keyed to the ids
    /// the restore produced. <paramref name="desktopRemap"/> maps a captured desktop GUID → the live desktop
    /// GUID it resolved to (identity for a reused desktop, a fresh GUID for a re-created one);
    /// <paramref name="branchRemap"/> maps a captured branch id → the freshly-minted live branch id. Anything
    /// with no remap entry is skipped — the corresponding desktop/branch isn't part of the restored layout.
    /// </summary>
    public static void ApplyTo(SpatialState target, Snapshot snap,
        IReadOnlyDictionary<Guid, Guid> desktopRemap, IReadOnlyDictionary<Guid, Guid> branchRemap)
    {
        foreach ((string key, string hex) in snap.GroupColors)
            if (Guid.TryParse(key, out Guid oldId) && branchRemap.TryGetValue(oldId, out Guid newId))
                target.SetColor(newId, hex);

        foreach ((string key, GridPos pos) in snap.Positions)
            if (Guid.TryParse(key, out Guid oldId) && desktopRemap.TryGetValue(oldId, out Guid newId))
                target.SetPosition(newId, pos);
    }

    private static IEnumerable<PersistedDesktop> AllDesktops(Snapshot snap) =>
        snap.MainDesktops.Concat(snap.Branches.SelectMany(b => b.Desktops));
}
