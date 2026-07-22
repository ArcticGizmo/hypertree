using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the carousel Model P against a fake controller: a top row of ungrouped desktops, groups
/// you dive into from anywhere, wrapping rotation between groups, resume-last-used, surface-to-top,
/// and click-to-navigate — before any hotkey/Win32 exists.
/// </summary>
public class NavigationModelTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId T0 = D(0), T1 = D(1), T2 = D(2);
    private static readonly DesktopId[] TopIds = { T0, T1, T2 };

    private static Group G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private static readonly (int, string)[] G1 = { (10, "a"), (11, "b"), (12, "c") };
    private static readonly (int, string)[] G2 = { (20, "x"), (21, "y") };

    private static (NavigationModel m, FakeDesktopController c) New(int current = 0)
    {
        var c = new FakeDesktopController(TopIds, current);
        return (new NavigationModel(c), c);
    }

    // ── Top row ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Starts_on_the_os_current_top_desktop()
    {
        var (m, c) = New(current: 1);
        Assert.True(m.OnTop);
        Assert.Empty(c.Switches);
        Assert.True(m.BuildMap().TopRow[1].IsCurrent);
    }

    [Fact]
    public void MoveRight_and_left_walk_the_top_row_and_clamp()
    {
        var (m, c) = New(current: 0);
        Assert.True(m.Apply(NavAction.MoveRight));
        Assert.Equal(T1, c.Current);
        Assert.True(m.Apply(NavAction.MoveLeft)); // move back to T0
        Assert.Equal(T0, c.Current);
        Assert.False(m.Apply(NavAction.MoveLeft)); // clamp at left edge — no-op
    }

    [Fact]
    public void Dive_with_no_groups_is_a_noop()
    {
        var (m, c) = New();
        Assert.False(m.Apply(NavAction.Dive));
        Assert.Empty(c.Switches);
        Assert.True(m.OnTop);
    }

    // ── Dive / surface into a single group ──────────────────────────────────────

    [Fact]
    public void Dive_enters_the_active_group_from_any_top_desktop()
    {
        var (m, c) = New(current: 2); // on T2, not any "anchor"
        m.AddGroup(G("feat", G1));
        Assert.True(m.Apply(NavAction.Dive));
        Assert.False(m.OnTop);
        Assert.Equal(D(10), c.Current); // group's first desktop
    }

    [Fact]
    public void Surface_returns_to_the_top_desktop_you_left()
    {
        var (m, c) = New(current: 2);
        m.AddGroup(G("feat", G1));
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.MoveRight); // into the group
        Assert.True(m.Apply(NavAction.Surface));
        Assert.True(m.OnTop);
        Assert.Equal(T2, c.Current); // back where we dived from
    }

    [Fact]
    public void Rediving_resumes_the_last_used_desktop()
    {
        var (m, c) = New();
        m.AddGroup(G("feat", G1));
        m.Apply(NavAction.Dive);        // a
        m.Apply(NavAction.MoveRight);   // b (last used)
        m.Apply(NavAction.Surface);
        c.Switches.Clear();
        Assert.True(m.Apply(NavAction.Dive));
        Assert.Equal(D(11), c.Current); // resumed at b, not a
    }

    // ── Fixed ladder across multiple groups ─────────────────────────────────────

    [Fact]
    public void New_group_is_inserted_at_the_top_of_the_stack()
    {
        var (m, c) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // newest goes to the front (nearest)
        m.Apply(NavAction.Dive);  // Down from top -> group 0 = "two"
        Assert.Equal(D(20), c.Current);
        Assert.Equal("two", m.BuildMap().Groups[0].Name);
    }

    [Fact]
    public void Down_steps_deeper_through_the_fixed_stack_without_reordering()
    {
        var (m, c) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // stack: [two, one]
        m.Apply(NavAction.Dive);  // -> two (level 1)
        Assert.Equal(D(20), c.Current);
        Assert.True(m.Apply(NavAction.Dive)); // -> one (level 2), stack unchanged
        Assert.Equal(D(10), c.Current);
        Assert.False(m.Apply(NavAction.Dive)); // at the bottom — no wrap
        // Order never changed:
        Assert.Equal(new[] { "two", "one" }, m.BuildMap().Groups.Select(g => g.Name));
    }

    [Fact]
    public void Up_steps_back_one_level_at_a_time()
    {
        var (m, c) = New(current: 1);
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // stack: [two, one]
        m.Apply(NavAction.Dive);  // two
        m.Apply(NavAction.Dive);  // one
        Assert.True(m.Apply(NavAction.Surface)); // back up to two
        Assert.Equal(D(20), c.Current);
        Assert.True(m.Apply(NavAction.Surface)); // back to the top row
        Assert.True(m.OnTop);
        Assert.Equal(T1, c.Current);
    }

    [Fact]
    public void PrepareForMapOpen_brings_the_last_used_group_to_the_top()
    {
        var (m, _) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // stack: [two, one]
        m.Apply(NavAction.Dive);  // two
        m.Apply(NavAction.Dive);  // one  -> last-used group is "one"
        m.Apply(NavAction.Surface);
        m.Apply(NavAction.Surface); // back on top

        m.PrepareForMapOpen();
        Assert.Equal("one", m.BuildMap().Groups[0].Name); // last-used floated to the top
    }

    // ── Click-to-navigate ────────────────────────────────────────────────────────

    [Fact]
    public void GoToTop_jumps_to_a_specific_top_desktop()
    {
        var (m, c) = New();
        m.AddGroup(G("feat", G1));
        m.Apply(NavAction.Dive);
        Assert.True(m.GoToTop(2));
        Assert.True(m.OnTop);
        Assert.Equal(T2, c.Current);
    }

    [Fact]
    public void GoToGroupDesktop_jumps_into_a_group_and_desktop()
    {
        var (m, c) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // stack: [two(0), one(1)]
        Assert.True(m.GoToGroupDesktop(1, 2)); // group "one", desktop c
        Assert.False(m.OnTop);
        Assert.Equal((1, 2), m.CurrentGroupDesktop);
        Assert.Equal(D(12), c.Current);
    }

    // ── Group removal ──────────────────────────────────────────────────────────

    [Fact]
    public void RemoveGroup_returns_it_and_surfaces_when_none_remain()
    {
        var (m, _) = New();
        m.AddGroup(G("feat", G1));
        m.Apply(NavAction.Dive);
        Group? removed = m.RemoveGroup(0);
        Assert.Equal("feat", removed!.Name);
        Assert.Equal(0, m.GroupCount);
        Assert.True(m.OnTop);
        Assert.False(m.Apply(NavAction.Dive)); // nothing to dive into now
    }

    // ── Single-desktop deletion ──────────────────────────────────────────────────

    [Fact]
    public void DetachGroupDesktop_removes_one_desktop_and_keeps_the_group()
    {
        var (m, _) = New();
        m.AddGroup(G("feat", G1)); // a, b, c
        DesktopId? id = m.DetachGroupDesktop(0, 1); // remove b
        Assert.Equal(D(11), id);
        Assert.Equal(1, m.GroupCount);
        Assert.Equal(new[] { "a", "c" }, m.BuildMap().Groups[0].Desktops.Select(t => t.Label));
    }

    [Fact]
    public void DetachGroupDesktop_removes_the_group_when_its_last_desktop_goes()
    {
        var (m, _) = New();
        m.AddGroup(G("solo", (30, "only")));
        DesktopId? id = m.DetachGroupDesktop(0, 0);
        Assert.Equal(D(30), id);
        Assert.Equal(0, m.GroupCount);
    }

    [Fact]
    public void PeekTopDesktop_exposes_id_and_label_for_a_confirm_prompt()
    {
        var (m, _) = New();
        var peek = m.PeekTopDesktop(1);
        Assert.NotNull(peek);
        Assert.Equal(T1, peek!.Value.id);
    }
}
