using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the vertical "stable pivot" Model P (F2) against a fake controller: a main timeline that
/// sits at a fixed slot in a never-reordering group stack, an Up/Down ladder that walks a cursor
/// through the sequence and crosses main <em>in place</em> (main never leaps), resume-last-used, edge
/// clamps, and click-to-navigate — before any hotkey/Win32 exists.
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

    // With no store, mainSlot defaults to 0 — main on top, groups below. AddGroup inserts directly
    // below main, so add C, B, A to get the fixed list [A, B, C] with A nearest main.
    private static void ThreeGroups(NavigationModel m)
    {
        m.AddGroup(G("C", G3));
        m.AddGroup(G("B", G2));
        m.AddGroup(G("A", G1)); // stack (below main): [A, B, C]
    }

    private sealed class InMemoryStore : IStateStore
    {
        public PersistedState State;
        public InMemoryStore(PersistedState s) => State = s;
        public PersistedState Load() => State;
        public void Save(PersistedState s) => State = s;
    }

    private static PersistedDesktop PD(int id, string label) => new() { Id = D(id).Value, Label = label };

    // A pivot layout persisted with main between two groups: feat-1 above main, feat-2 below (slot 1).
    private static (NavigationModel m, FakeDesktopController c) Pivot()
    {
        var ids = new[] { T0, T1, T2, D(10), D(11), D(12), D(20), D(21) };
        var state = new PersistedState
        {
            MainSlot = 1, ActiveGroup = 0,
            Groups =
            {
                new PersistedGroup { Name = "feat-1", Desktops = { PD(10, "a"), PD(11, "b"), PD(12, "c") } },
                new PersistedGroup { Name = "feat-2", Desktops = { PD(20, "x"), PD(21, "y") } },
            },
        };
        var c = new FakeDesktopController(ids, 0); // OS current = T0 (a main-timeline desktop)
        return (new NavigationModel(c, new InMemoryStore(state)), c);
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
    public void Surface_from_main_with_nothing_above_is_a_noop()
    {
        var (m, _) = New();
        m.AddGroup(G("only", G1)); // mainSlot 0 → main on top, nothing above it
        Assert.False(m.Apply(NavAction.Surface));
        Assert.True(m.OnTop);
    }

    // ── Down / Up ladder into a single group ────────────────────────────────────

    [Fact]
    public void Down_from_main_enters_the_group_below()
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

    // ── The ladder across a fixed [A, B, C] stack below main ─────────────────────

    [Fact]
    public void New_group_is_inserted_directly_below_main()
    {
        var (m, c) = New();
        m.AddGroup(G("one", G1));
        m.AddGroup(G("two", G2)); // inserted directly below main → nearest
        Assert.True(m.Apply(NavAction.Dive)); // Down from main enters the group below = "two"
        Assert.Equal(D(20), c.Current);
        Assert.Equal("two", m.BuildMap().Groups[0].Name);
    }

    [Fact]
    public void Down_steps_through_the_stack_one_group_at_a_time()
    {
        var (m, c) = New();
        ThreeGroups(m); // MAIN / A / B / C
        Assert.True(m.Apply(NavAction.Dive)); // A
        Assert.Equal(D(10), c.Current);
        Assert.True(m.Apply(NavAction.Dive)); // B
        Assert.Equal(D(20), c.Current);
        Assert.True(m.Apply(NavAction.Dive)); // C
        Assert.Equal(D(30), c.Current);
        Assert.False(m.Apply(NavAction.Dive)); // clamp at the bottom
    }

    [Fact]
    public void Up_steps_back_up_the_stack_toward_main()
    {
        var (m, c) = New(current: 1);
        ThreeGroups(m);
        m.GoToGroupDesktop(2, 0); // in C
        c.Switches.Clear();
        Assert.True(m.Apply(NavAction.Surface)); // C → B
        Assert.Equal(D(20), c.Current);
        Assert.True(m.Apply(NavAction.Surface)); // B → A
        Assert.Equal(D(10), c.Current);
        Assert.True(m.Apply(NavAction.Surface)); // A → main
        Assert.True(m.OnTop);
        Assert.Equal(T1, c.Current);
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

    // ── The stable pivot: main sits between groups and never moves ───────────────

    [Fact]
    public void Pivot_renders_groups_above_and_below_a_fixed_main_slot()
    {
        var (m, _) = Pivot();
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.Equal(1, map.TopPosition); // feat-1 above main, feat-2 below
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Up_from_main_enters_the_group_above_without_moving_main()
    {
        var (m, c) = Pivot(); // feat-1 / MAIN / feat-2, cursor on main
        Assert.True(m.Apply(NavAction.Surface)); // ↑ → into feat-1 (above)
        Assert.False(m.OnTop);
        Assert.Equal((0, 0), m.CurrentGroupDesktop);
        Assert.Equal(D(10), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition); // main did NOT leap — still slot 1
    }

    [Fact]
    public void Down_from_main_enters_the_group_below_without_moving_main()
    {
        var (m, c) = Pivot();
        Assert.True(m.Apply(NavAction.Dive)); // ↓ → into feat-2 (below)
        Assert.False(m.OnTop);
        Assert.Equal((1, 0), m.CurrentGroupDesktop);
        Assert.Equal(D(20), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition);
    }

    [Fact]
    public void Crossing_main_from_below_to_above_keeps_the_stack_stable()
    {
        var (m, _) = Pivot();
        m.GoToGroupDesktop(1, 0);                 // in feat-2 (below main)
        Assert.True(m.Apply(NavAction.Surface));  // feat-2 → main
        Assert.True(m.OnTop);
        Assert.True(m.Apply(NavAction.Surface));  // main → feat-1 (above)
        Assert.Equal((0, 0), m.CurrentGroupDesktop);
        Assert.Equal(1, m.BuildMap().TopPosition); // whole sequence unchanged throughout
    }

    [Fact]
    public void New_group_appears_below_main_leaving_the_above_groups_in_place()
    {
        var (m, _) = Pivot(); // feat-1 above (slot 1), feat-2 below
        m.AddGroup(G("hotfix", G3));
        NavMap map = m.BuildMap();
        Assert.Equal(1, map.TopPosition); // still one group above main
        Assert.Equal(new[] { "feat-1", "hotfix", "feat-2" }, map.Groups.Select(g => g.Name));
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
    public void GoToGroupDesktop_jumps_into_a_group_and_desktop_without_moving_main()
    {
        var (m, c) = Pivot();
        Assert.True(m.GoToGroupDesktop(1, 1)); // feat-2, desktop y
        Assert.False(m.OnTop);
        Assert.Equal((1, 1), m.CurrentGroupDesktop);
        Assert.Equal(D(21), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition);
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
    public void Removing_a_group_above_main_raises_the_main_slot_to_match()
    {
        var (m, _) = Pivot();      // feat-1 above main (slot 1), feat-2 below
        m.RemoveGroup(0);          // drop feat-1 (above) → main rises to slot 0
        NavMap map = m.BuildMap();
        Assert.Equal(0, map.TopPosition);
        Assert.Equal(new[] { "feat-2" }, map.Groups.Select(g => g.Name));
    }

    // ── Reconcile against externally-deleted desktops ───────────────────────────

    [Fact]
    public void Reconcile_drops_group_desktops_the_os_no_longer_has()
    {
        var (m, c) = Pivot(); // feat-1: a,b,c (10,11,12); feat-2: x,y
        c.Remove(D(11), T0);  // user deletes feat-1's "b" from Task View
        m.Reconcile();
        var g = m.BuildMap().Groups.First(x => x.Name == "feat-1");
        Assert.Equal(new[] { "a", "c" }, g.Desktops.Select(t => t.Label));
    }

    [Fact]
    public void Reconcile_removes_a_group_whose_desktops_all_vanish()
    {
        var (m, c) = Pivot();
        c.Remove(D(20), T0);
        c.Remove(D(21), T0); // feat-2 entirely gone from the OS
        m.Reconcile();
        Assert.Equal(new[] { "feat-1" }, m.BuildMap().Groups.Select(g => g.Name));
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

    // ── Snapshots (capture / restore a whole named layout) ───────────────────────

    [Fact]
    public void CaptureSnapshot_records_the_main_slot_main_desktops_and_groups()
    {
        var (m, _) = Pivot(); // feat-1 above main (slot 1), feat-2 below; main = T0,T1,T2
        Snapshot snap = m.CaptureSnapshot("layout-1");

        Assert.Equal("layout-1", snap.Name);
        Assert.Equal(1, snap.MainSlot);
        Assert.Equal(8, snap.DesktopCount); // 3 main + 3 + 2
        Assert.Equal(new[] { T0.Value, T1.Value, T2.Value }, snap.MainDesktops.Select(d => d.Id));
        Assert.Equal(new[] { "feat-1", "feat-2" }, snap.Groups.Select(g => g.Name));
        Assert.Equal(new[] { D(10).Value, D(11).Value, D(12).Value }, snap.Groups[0].Desktops.Select(d => d.Id));
        Assert.Equal(new[] { "a", "b", "c" }, snap.Groups[0].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void RestoreStructure_replaces_groups_and_rederives_the_main_timeline()
    {
        var (_, c) = Pivot();
        // A fresh model over the same OS (no persisted groups) sees all 8 desktops as ungrouped.
        var fresh = new NavigationModel(c);
        Assert.Equal(8, fresh.BuildMap().TopRow.Count);
        Assert.Empty(fresh.BuildMap().Groups);

        fresh.RestoreStructure(1, new[] { G("feat-1", G1), G("feat-2", G2) });

        NavMap map = fresh.BuildMap();
        Assert.Equal(3, map.TopRow.Count);  // only T0,T1,T2 stay on the main timeline now
        Assert.Equal(1, map.TopPosition);   // the restored main slot is honoured
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Snapshot_round_trips_through_capture_and_restore()
    {
        var (source, c) = Pivot();
        Snapshot snap = source.CaptureSnapshot("s");

        var target = new NavigationModel(c); // same OS, no persisted groups
        var groups = snap.Groups
            .Select(g => new Group(g.Name,
                g.Desktops.Select(d => new DesktopRef(new DesktopId(d.Id), d.Label)).ToList(),
                g.LastUsedIndex))
            .ToList();
        target.RestoreStructure(snap.MainSlot, groups);

        NavMap a = source.BuildMap(), b = target.BuildMap();
        Assert.Equal(a.TopPosition, b.TopPosition);
        Assert.Equal(a.TopRow.Count, b.TopRow.Count);
        Assert.Equal(a.Groups.Select(g => g.Name), b.Groups.Select(g => g.Name));
        Assert.Equal(
            a.Groups.SelectMany(g => g.Desktops.Select(d => d.Label)),
            b.Groups.SelectMany(g => g.Desktops.Select(d => d.Label)));
    }

    // ── Per-desktop window counts on the map ─────────────────────────────────────

    [Fact]
    public void BuildMap_carries_per_desktop_window_counts_onto_the_tiles()
    {
        var (m, c) = Pivot();
        c.WinCounts[T0] = 4;     // a main desktop
        c.WinCounts[D(10)] = 2;  // feat-1's "a"
        // D(11) ("b") and everything else left unset → 0

        NavMap map = m.BuildMap();
        Assert.Equal(4, map.TopRow[0].WindowCount);
        Assert.Equal(0, map.TopRow[1].WindowCount); // unset desktop reads as empty

        NavMapGroup feat1 = map.Groups.First(g => g.Name == "feat-1");
        Assert.Equal(2, feat1.Desktops[0].WindowCount);
        Assert.Equal(0, feat1.Desktops[1].WindowCount);
    }
}
