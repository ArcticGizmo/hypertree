using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>Covers the render-ready snapshot the overlay/flash draw from.</summary>
public class NavMapTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId[] TopIds = { D(0), D(1), D(2) };

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private static NavigationModel New(int current = 0) => new(new FakeDesktopController(TopIds, current));

    [Fact]
    public void Main_timeline_lists_all_unbranched_desktops_with_current_marked()
    {
        var m = New(current: 1);
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.Equal(3, map.TopRow.Count);
        Assert.Equal(new[] { false, true, false }, map.TopRow.Select(t => t.IsCurrent));
        Assert.Empty(map.Branches);
        Assert.Equal(0, map.TopPosition);
    }

    [Fact]
    public void Branches_are_listed_in_fixed_stack_order_newest_on_top()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a")));
        m.AddBranch(G("two", (20, "x"))); // inserted at the front (nearest)
        NavMap map = m.BuildMap();

        Assert.Equal(2, map.Branches.Count);
        Assert.Equal("two", map.Branches[0].Name);
        Assert.Equal(0, map.Branches[0].Index); // Index == list position (no rotation)
        Assert.Equal("one", map.Branches[1].Name);
        Assert.Equal(1, map.Branches[1].Index);
    }

    [Fact]
    public void On_top_no_branch_is_current_level()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a"), (11, "b")));
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.All(map.Branches, g => Assert.False(g.IsCurrentLevel));
        Assert.All(map.Branches[0].Desktops, d => Assert.False(d.IsCurrent));
    }

    [Fact]
    public void Inside_a_branch_marks_it_current_with_its_current_desktop()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a"), (11, "b")));
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.MoveRight); // b
        NavMap map = m.BuildMap();

        Assert.False(map.OnTop);
        Assert.True(map.Branches[0].IsCurrentLevel);
        Assert.Equal(new[] { false, true }, map.Branches[0].Desktops.Select(d => d.IsCurrent));
        Assert.All(map.TopRow, t => Assert.False(t.IsCurrent)); // nothing on the main timeline is current
    }

    [Fact]
    public void SetDesktopLabel_relabels_a_main_timeline_desktop()
    {
        var m = New();
        m.SetDesktopLabel(onMain: true, branchIndex: -1, desktopIndex: 1, "planning");

        NavMap map = m.BuildMap();
        Assert.Equal("planning", map.TopRow[1].Label);
        Assert.Equal((null, "planning"), m.Describe(TopIds[1]));
    }

    [Fact]
    public void SetDesktopLabel_relabels_a_branch_desktop_leaving_others_untouched()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a"), (11, "b")));
        m.SetDesktopLabel(onMain: false, branchIndex: 0, desktopIndex: 1, "editor");

        NavMap map = m.BuildMap();
        Assert.Equal(new[] { "a", "editor" }, map.Branches[0].Desktops.Select(d => d.Label));
        Assert.Equal(("one", "editor"), m.Describe(D(11)));
    }

    [Fact]
    public void SetDesktopLabel_ignores_out_of_range_indices()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a")));
        m.SetDesktopLabel(onMain: true, branchIndex: -1, desktopIndex: 9, "x");   // no such top desktop
        m.SetDesktopLabel(onMain: false, branchIndex: 5, desktopIndex: 0, "x");   // no such branch
        m.SetDesktopLabel(onMain: false, branchIndex: 0, desktopIndex: 9, "x");   // no such branch desktop

        NavMap map = m.BuildMap();
        Assert.Equal(new[] { "d0", "d1", "d2" }, map.TopRow.Select(t => t.Label));
        Assert.Equal(new[] { "a" }, map.Branches[0].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void TopPosition_is_the_fixed_main_slot_unaffected_by_entering_a_branch()
    {
        var m = New();
        m.AddBranch(G("one", (10, "a")));
        m.AddBranch(G("two", (20, "x"))); // mainSlot 0 → main on top, both below
        m.GoToBranchDesktop(1, 0);        // enter "one" — main must NOT move

        NavMap map = m.BuildMap();
        Assert.Equal(0, map.TopPosition); // MAIN / two / one
    }
}
