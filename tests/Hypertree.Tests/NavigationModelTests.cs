using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the vertical "main-above-current" Model P (F2) against a fake controller: a main
/// timeline of ungrouped desktops as the pivot, a fixed group stack that never reorders, the
/// asymmetric Up/Down transitions (Up passes through main, Down goes straight to the next group),
/// resume-last-used, edge clamps, and click-to-navigate — before any hotkey/Win32 exists.
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
    private static readonly (int, string)[] G3 = { (30, "p"), (31, "q") };

    private static (NavigationModel m, FakeDesktopController c) New(int current = 0)
    {
        var c = new FakeDesktopController(TopIds, current);
        return (new NavigationModel(c), c);
    }

    // Build stack [A, B, C] (fixed listed order). AddGroup inserts at the front, so add C, B, A.
    private static void ThreeGroups(NavigationModel m)
    {
        m.AddGroup(G("C", G3));
        m.AddGroup(G("B", G2));
        m.AddGroup(G("A", G1)); // stack now [A, B, C]
    }

    // ── Main timeline ─────────────────────────────────────────────────────────────

    [Fact]
    public void Starts_on_the_os_current_main_desktop()
    {
        var (m, c) = New(current: 1);
        Assert.True(m.OnTop);
        Assert.Empty(c.Switches);
        Assert.True(m.BuildMap().TopRow[1].IsCurrent);
    }

    [Fact]
    public void MoveRight_and_left_walk_the_main_timeline_and_clamp()
    {
        var (m, c) = New(current: 0);
        Assert.True(m.Apply(NavAction.MoveRight));
        Assert.Equal(T1, c.Current);
        Assert.True(m.Apply(NavAction.MoveLeft));
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

    [Fact]
    public void Surface_from_main_with_no_group_above_is_a_noop()
    {
        var (m, _) = New();
        m.AddGroup(G("only", G1)); // currentGroup = 0, nothing above main
        Assert.False(m.Apply(NavAction.Surface));
        Assert.True(m.OnTop);
    }

    // ── Down / Up into a single group ───────────────────────────────────────────

    [Fact]
    public void Down_from_main_enters_the_current_group_from_any_desktop()
    {
        var (m, c) = New(current: 2); // on T2
        m.AddGroup(G("feat", G1));
        Assert.True(m.Apply(NavAction.Dive));
        Assert.False(m.OnTop);
        Assert.Equal(D(10), c.Current); // group's first desktop
    }

    [Fact]
    public void Surface_returns_to_the_main_desktop_you_left()
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

    // ── The vertical model across a fixed [A, B, C] stack ────────────────────────

    [Fact]
    public void New_group_becomes_the_dive_target_directly_below_main()
    {
        var (m, c) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // newest inserted at the front
        Assert.True(m.Apply(NavAction.Dive)); // Down from main enters the group below main = "two"
        Assert.Equal(D(20), c.Current);
        Assert.Equal("two", m.BuildMap().Groups[0].Name);
    }

    [Fact]
    public void Down_in_a_group_goes_straight_to_the_next_group_without_recrossing_main()
    {
        var (m, c) = New();
        ThreeGroups(m); // [A, B, C], currentGroup = 0 (A below main)
        m.GoToGroupDesktop(1, 0); // sit in B (current-below-main = A/MAIN/B/C? no — GoTo sets currentGroup=1)
        c.Switches.Clear();
        Assert.True(m.Apply(NavAction.Dive)); // Down from B → C, straight (no main)
        Assert.Equal(D(30), c.Current);       // C's first desktop
        Assert.Equal((2, 0), m.CurrentGroupDesktop);
    }

    [Fact]
    public void Up_from_a_group_passes_through_main_before_the_previous_group()
    {
        var (m, c) = New();
        ThreeGroups(m);
        m.GoToGroupDesktop(1, 0); // in B, currentGroup = 1
        c.Switches.Clear();

        Assert.True(m.Apply(NavAction.Surface)); // B → MAIN
        Assert.True(m.OnTop);
        Assert.True(m.Apply(NavAction.Surface)); // MAIN → A (previous group)
        Assert.False(m.OnTop);
        Assert.Equal(D(10), c.Current);          // A's resume desktop
        Assert.Equal((0, 0), m.CurrentGroupDesktop);
    }

    [Fact]
    public void Down_at_the_last_group_clamps()
    {
        var (m, _) = New();
        ThreeGroups(m);
        m.GoToGroupDesktop(2, 0); // in C (the last group)
        Assert.False(m.Apply(NavAction.Dive)); // no wrap past the bottom
        Assert.Equal((2, 0), m.CurrentGroupDesktop);
    }

    [Fact]
    public void Stack_never_reorders_as_you_navigate()
    {
        var (m, _) = New();
        ThreeGroups(m);
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.Surface);
        Assert.Equal(new[] { "A", "B", "C" }, m.BuildMap().Groups.Select(g => g.Name));
    }

    [Fact]
    public void TopPosition_tracks_the_current_group_so_main_sits_directly_above_it()
    {
        var (m, _) = New();
        ThreeGroups(m); // currentGroup = 0
        Assert.Equal(0, m.BuildMap().TopPosition); // MAIN / A / B / C

        m.GoToGroupDesktop(1, 0);
        Assert.Equal(1, m.BuildMap().TopPosition); // A / MAIN / B / C

        m.GoToGroupDesktop(2, 0);
        Assert.Equal(2, m.BuildMap().TopPosition); // A / B / MAIN / C
    }

    // ── Click-to-navigate ────────────────────────────────────────────────────────

    [Fact]
    public void GoToTop_jumps_to_a_specific_main_desktop()
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
        m.AddGroup(G("two", G2)); // stack [two(0), one(1)]
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

    [Fact]
    public void RemoveGroup_above_the_current_one_keeps_you_on_the_same_group()
    {
        var (m, _) = New();
        ThreeGroups(m);
        m.GoToGroupDesktop(2, 1); // in C, currentGroup = 2
        m.RemoveGroup(0);         // drop A → stack [B, C], C now at index 1
        Assert.False(m.OnTop);
        Assert.Equal((1, 1), m.CurrentGroupDesktop); // still C, still on its resume desktop
    }

    [Fact]
    public void RemoveGroup_you_are_inside_surfaces_to_main()
    {
        var (m, _) = New();
        ThreeGroups(m);
        m.GoToGroupDesktop(1, 0); // in B
        m.RemoveGroup(1);         // remove B
        Assert.True(m.OnTop);
        Assert.Equal(new[] { "A", "C" }, m.BuildMap().Groups.Select(g => g.Name));
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
