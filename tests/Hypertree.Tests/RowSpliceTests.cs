using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Locks the one row-order splice both <see cref="NavigationModel"/> and <see cref="SpatialSnapshot"/> now
/// share: branch indices with main spliced in at a clamped slot. Guards the off-by-one / clamp that used to
/// be hand-rebuilt (and had drifted) at each site.
/// </summary>
public sealed class RowSpliceTests
{
    [Fact]
    public void Main_is_spliced_in_at_its_slot()
    {
        Assert.Equal(new[] { 0, 1, RowSplice.MainMarker, 2, 3 }, RowSplice.Order(count: 4, mainSlot: 2));
    }

    [Fact]
    public void Slot_zero_puts_main_first_and_full_slot_puts_it_last()
    {
        Assert.Equal(new[] { RowSplice.MainMarker, 0, 1, 2 }, RowSplice.Order(3, 0));
        Assert.Equal(new[] { 0, 1, 2, RowSplice.MainMarker }, RowSplice.Order(3, 3));
    }

    [Fact]
    public void No_branches_is_main_alone()
    {
        Assert.Equal(new[] { RowSplice.MainMarker }, RowSplice.Order(0, 0));
        Assert.Equal(new[] { RowSplice.MainMarker }, RowSplice.Order(0, 5)); // slot clamps to 0
    }

    [Theory]
    [InlineData(-3)]  // below range → clamps to 0 (main first)
    [InlineData(99)]  // above range → clamps to count (main last)
    public void Out_of_range_slot_clamps_without_dropping_or_duplicating_a_branch(int slot)
    {
        IReadOnlyList<int> order = RowSplice.Order(count: 3, mainSlot: slot);

        // Exactly one main marker, and every branch index present once.
        Assert.Single(order, x => x == RowSplice.MainMarker);
        Assert.Equal(new[] { 0, 1, 2 }, order.Where(x => x != RowSplice.MainMarker).OrderBy(x => x));
        Assert.Equal(4, order.Count);
    }
}
