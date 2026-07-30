using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>Covers the breadcrumb trail behind Ctrl+Alt+Z / Ctrl+Alt+Shift+Z: transaction-end recording,
/// undo/redo stepping, forward-tail truncation, pruning of deleted desktops, and the capacity cap.</summary>
public class NavHistoryTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    [Fact]
    public void First_record_seeds_the_origin_so_undo_can_return_to_it()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));

        Assert.Equal(new[] { D(1), D(2) }, h.Entries);
        Assert.Equal(D(2), h.Current);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Recording_where_you_already_are_is_a_no_op()
    {
        var h = new NavHistory();
        h.Record(D(1), D(1));
        Assert.Empty(h.Entries);
        Assert.Null(h.Current);
    }

    [Fact]
    public void Undo_walks_back_and_redo_walks_forward_without_rewriting_the_trail()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));

        Assert.Equal(D(2), h.Undo());
        Assert.Equal(D(1), h.Undo());
        Assert.Null(h.Undo()); // start of the trail
        Assert.Equal(D(2), h.Redo());
        Assert.Equal(D(3), h.Redo());
        Assert.Null(h.Redo()); // end of the trail
        Assert.Equal(new[] { D(1), D(2), D(3) }, h.Entries); // untouched throughout
    }

    [Fact]
    public void Navigating_from_mid_trail_truncates_the_redo_tail()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));
        h.Undo(); // back to 2
        h.Record(D(2), D(4)); // a real move branches history

        Assert.Equal(new[] { D(1), D(2), D(4) }, h.Entries);
        Assert.Equal(D(4), h.Current);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void A_transaction_starting_off_trail_reconnects_through_its_origin()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        // An external switch moved us to 5 unrecorded; the next transaction runs 5 → 6.
        h.Record(D(5), D(6));

        Assert.Equal(new[] { D(1), D(2), D(5), D(6) }, h.Entries);
        Assert.Equal(D(6), h.Current);
    }

    [Fact]
    public void Prune_drops_dead_desktops_and_collapses_the_duplicates_left_behind()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(1));
        h.Record(D(1), D(3)); // trail: 1 2 1 3

        h.Prune(id => id != D(2)); // desktop 2 was deleted

        Assert.Equal(new[] { D(1), D(3) }, h.Entries); // 1 2 1 collapses to a single 1
        Assert.Equal(D(3), h.Current);                 // cursor stayed on its entry
    }

    [Fact]
    public void Prune_moves_the_cursor_to_the_nearest_survivor_when_its_entry_dies()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));
        h.Undo(); // cursor on 2

        h.Prune(id => id != D(2));

        Assert.Equal(new[] { D(1), D(3) }, h.Entries);
        Assert.Equal(D(1), h.Current); // the surviving entry behind the dead cursor
        Assert.True(h.CanRedo);        // 3 is still ahead
    }

    [Fact]
    public void Trail_is_capped_by_dropping_the_oldest_entries()
    {
        var h = new NavHistory(capacity: 3);
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));
        h.Record(D(3), D(4));

        Assert.Equal(new[] { D(2), D(3), D(4) }, h.Entries);
        Assert.Equal(D(4), h.Current);
    }

    [Fact]
    public void Toggle_bounces_between_the_two_newest_entries()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3)); // trail: 1 2 3 — the bounce pair is 2 ↔ 3

        Assert.Equal(D(2), h.Toggle(D(3))); // standing on the newest → hop to the one before
        Assert.Equal(D(2), h.Current);      // the cursor follows the hop
        Assert.Equal(D(3), h.Toggle(D(2))); // and back again, for ever
        Assert.Equal(D(3), h.Current);
        Assert.Equal(new[] { D(1), D(2), D(3) }, h.Entries); // the trail itself never rewrites
    }

    [Fact]
    public void Toggle_from_off_trail_lands_on_the_newest_entry()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));

        Assert.Equal(D(3), h.Toggle(D(9))); // an external switch moved us off-trail — hop to the newest
    }

    [Fact]
    public void Toggle_needs_two_entries()
    {
        var h = new NavHistory();
        Assert.Null(h.Toggle(D(1)));

        h.Record(D(1), D(2));
        h.Prune(id => id == D(2)); // a one-entry trail (desktop 1 deleted)
        Assert.Null(h.Toggle(D(2)));
    }

    [Fact]
    public void Navigating_after_a_toggle_branches_from_where_the_hop_left_you()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));
        h.Toggle(D(3));       // hopped back to 2
        h.Record(D(2), D(4)); // a real move from there truncates 3 off the tail

        Assert.Equal(new[] { D(1), D(2), D(4) }, h.Entries);
        Assert.Equal(D(4), h.Current);
    }

    [Fact]
    public void Undo_then_redo_lands_back_where_you_were()
    {
        var h = new NavHistory();
        h.Record(D(1), D(2));
        h.Record(D(2), D(3));
        h.Undo();
        h.Undo();
        Assert.Equal(D(1), h.Current);
        h.Redo();
        h.Redo();
        Assert.Equal(D(3), h.Current);
    }
}
