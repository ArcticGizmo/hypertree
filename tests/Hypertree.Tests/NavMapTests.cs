using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>Covers the render-ready snapshot the overlay/flash draw from.</summary>
public class NavMapTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId[] TopIds = { D(0), D(1), D(2) };

    private static Group G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private static NavigationModel New(int current = 0) => new(new FakeDesktopController(TopIds, current));

    [Fact]
    public void Top_row_lists_all_ungrouped_desktops_with_current_marked()
    {
        var m = New(current: 1);
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.Equal(3, map.TopRow.Count);
        Assert.Equal(new[] { false, true, false }, map.TopRow.Select(t => t.IsCurrent));
        Assert.Empty(map.Groups);
    }

    [Fact]
    public void Groups_are_listed_in_fixed_stack_order_newest_on_top()
    {
        var m = New();
        m.AddGroup(G("one", (10, "a")));
        m.AddGroup(G("two", (20, "x"))); // inserted at the front (nearest)
        NavMap map = m.BuildMap();

        Assert.Equal(2, map.Groups.Count);
        Assert.Equal("two", map.Groups[0].Name);
        Assert.Equal(0, map.Groups[0].Index); // Index == list position (no rotation)
        Assert.Equal("one", map.Groups[1].Name);
        Assert.Equal(1, map.Groups[1].Index);
    }

    [Fact]
    public void On_top_no_group_is_current_level()
    {
        var m = New();
        m.AddGroup(G("one", (10, "a"), (11, "b")));
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.All(map.Groups, g => Assert.False(g.IsCurrentLevel));
        Assert.All(map.Groups[0].Desktops, d => Assert.False(d.IsCurrent));
    }

    [Fact]
    public void Dived_marks_the_active_group_and_its_current_desktop()
    {
        var m = New();
        m.AddGroup(G("one", (10, "a"), (11, "b")));
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.MoveRight); // b
        NavMap map = m.BuildMap();

        Assert.False(map.OnTop);
        Assert.True(map.Groups[0].IsCurrentLevel);
        Assert.Equal(new[] { false, true }, map.Groups[0].Desktops.Select(d => d.IsCurrent));
        Assert.All(map.TopRow, t => Assert.False(t.IsCurrent)); // nothing on the top row is current
    }
}
