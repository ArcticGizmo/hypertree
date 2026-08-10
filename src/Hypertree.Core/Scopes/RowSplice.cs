namespace Hypertree.Scopes;

/// <summary>
/// The one definition of the vertical stack's row order: branch indices <c>0..count-1</c> with the main
/// timeline spliced in at its slot — <c>branches[0..slot-1] / MAIN / branches[slot..]</c> — with the slot
/// clamped into range. The navigation model (its map/spatial/status projections and re-slotting) and a
/// snapshot preview both build their top-to-bottom order from here, so the "splice main at its slot"
/// invariant and its off-by-one clamp live in exactly one place rather than being hand-rebuilt at each site.
/// </summary>
internal static class RowSplice
{
    /// <summary>Stands in for the main timeline within a row-index sequence (shared with
    /// <see cref="NavProjection"/> via <see cref="NavigationModel.MainRowMarker"/>).</summary>
    public const int MainMarker = -1;

    /// <summary>Branch indices <c>[0, count)</c> in draw order with <see cref="MainMarker"/> spliced in at
    /// <c>Clamp(mainSlot, 0, count)</c> — how many branches sit above main.</summary>
    public static IReadOnlyList<int> Order(int count, int mainSlot)
    {
        int slot = Math.Clamp(mainSlot, 0, count);
        var seq = new List<int>(count + 1);
        for (int i = 0; i < slot; i++) seq.Add(i);
        seq.Add(MainMarker);
        for (int i = slot; i < count; i++) seq.Add(i);
        return seq;
    }
}
