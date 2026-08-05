using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the vertical "stable pivot" Model P (F2) against a fake controller: a main timeline that
/// sits at a fixed slot in a never-reordering branch stack, an Up/Down ladder that walks a cursor
/// through the sequence and crosses main <em>in place</em> (main never leaps), resume-last-used, edge
/// clamps, and click-to-navigate — before any hotkey/Win32 exists.
/// </summary>
public class NavigationModelTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId T0 = D(0), T1 = D(1), T2 = D(2);
    private static readonly DesktopId[] TopIds = { T0, T1, T2 };

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private static readonly (int, string)[] G1 = { (10, "a"), (11, "b"), (12, "c") };
    private static readonly (int, string)[] G2 = { (20, "x"), (21, "y") };
    private static readonly (int, string)[] G3 = { (30, "p"), (31, "q") };

    private static (NavigationModel m, FakeDesktopController c) New(int current = 0)
    {
        var c = new FakeDesktopController(TopIds, current);
        return (new NavigationModel(c), c);
    }

    // With no store, mainSlot defaults to 0 — main on top, branches below. AddBranch inserts directly
    // below main, so add C, B, A to get the fixed list [A, B, C] with A nearest main.
    private static void ThreeBranches(NavigationModel m)
    {
        m.AddBranch(G("C", G3));
        m.AddBranch(G("B", G2));
        m.AddBranch(G("A", G1)); // stack (below main): [A, B, C]
    }

    private sealed class InMemoryStore : IStateStore
    {
        public PersistedState State;
        public InMemoryStore(PersistedState s) => State = s;
        public PersistedState Load() => State;
        public void Save(PersistedState s) => State = s;
    }

    private static PersistedDesktop PD(int id, string label) => new() { Id = D(id).Value, Label = label };

    // A pivot layout persisted with main between two branches: feat-1 above main, feat-2 below (slot 1).
    private static (NavigationModel m, FakeDesktopController c) Pivot()
    {
        var ids = new[] { T0, T1, T2, D(10), D(11), D(12), D(20), D(21) };
        var state = new PersistedState
        {
            MainSlot = 1, ActiveBranch = 0,
            Branches =
            {
                new PersistedBranch { Name = "feat-1", Desktops = { PD(10, "a"), PD(11, "b"), PD(12, "c") } },
                new PersistedBranch { Name = "feat-2", Desktops = { PD(20, "x"), PD(21, "y") } },
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
    public void Describe_names_main_desktops_and_branch_prefixes()
    {
        var (m, _) = Pivot();
        // A main-timeline desktop: no branch, label is the OS name from the top row.
        Assert.Equal((null, "d0"), m.Describe(T0));
        // A branch desktop: prefixed with its branch name, using its in-branch label.
        Assert.Equal(("feat-1", "b"), m.Describe(D(11)));
        Assert.Equal(("feat-2", "y"), m.Describe(D(21)));
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
    public void Dive_with_no_branches_is_a_noop()
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
        m.AddBranch(G("only", G1)); // mainSlot 0 → main on top, nothing above it
        Assert.False(m.Apply(NavAction.Surface));
        Assert.True(m.OnTop);
    }

    // ── Down / Up ladder into a single branch ────────────────────────────────────

    [Fact]
    public void Down_from_main_enters_the_branch_below()
    {
        var (m, c) = New(current: 2); // on T2
        m.AddBranch(G("feat", G1));
        Assert.True(m.Apply(NavAction.Dive));
        Assert.False(m.OnTop);
        Assert.Equal(D(10), c.Current); // branch's first desktop
    }

    [Fact]
    public void Surface_returns_to_the_main_desktop_you_left()
    {
        var (m, c) = New(current: 2);
        m.AddBranch(G("feat", G1));
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.MoveRight); // into the branch
        Assert.True(m.Apply(NavAction.Surface));
        Assert.True(m.OnTop);
        Assert.Equal(T2, c.Current); // back where we dived from
    }

    [Fact]
    public void Rediving_resumes_the_last_used_desktop()
    {
        var (m, c) = New();
        m.AddBranch(G("feat", G1));
        m.Apply(NavAction.Dive);        // a
        m.Apply(NavAction.MoveRight);   // b (last used)
        m.Apply(NavAction.Surface);
        c.Switches.Clear();
        Assert.True(m.Apply(NavAction.Dive));
        Assert.Equal(D(11), c.Current); // resumed at b, not a
    }

    // ── The ladder across a fixed [A, B, C] stack below main ─────────────────────

    [Fact]
    public void New_branch_is_inserted_directly_below_main()
    {
        var (m, c) = New();
        m.AddBranch(G("one", G1));
        m.AddBranch(G("two", G2)); // inserted directly below main → nearest
        Assert.True(m.Apply(NavAction.Dive)); // Down from main enters the branch below = "two"
        Assert.Equal(D(20), c.Current);
        Assert.Equal("two", m.BuildMap().Branches[0].Name);
    }

    [Fact]
    public void Down_steps_through_the_stack_one_branch_at_a_time()
    {
        var (m, c) = New();
        ThreeBranches(m); // MAIN / A / B / C
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
        ThreeBranches(m);
        m.GoToBranchDesktop(2, 0); // in C
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
        ThreeBranches(m);
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.Dive);
        m.Apply(NavAction.Surface);
        Assert.Equal(new[] { "A", "B", "C" }, m.BuildMap().Branches.Select(g => g.Name));
    }

    // ── The stable pivot: main sits between branches and never moves ───────────────

    [Fact]
    public void Pivot_renders_branches_above_and_below_a_fixed_main_slot()
    {
        var (m, _) = Pivot();
        NavMap map = m.BuildMap();
        Assert.True(map.OnTop);
        Assert.Equal(1, map.TopPosition); // feat-1 above main, feat-2 below
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Branches.Select(g => g.Name));
    }

    [Fact]
    public void Up_from_main_enters_the_branch_above_without_moving_main()
    {
        var (m, c) = Pivot(); // feat-1 / MAIN / feat-2, cursor on main
        Assert.True(m.Apply(NavAction.Surface)); // ↑ → into feat-1 (above)
        Assert.False(m.OnTop);
        Assert.Equal((0, 0), m.CurrentBranchDesktop);
        Assert.Equal(D(10), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition); // main did NOT leap — still slot 1
    }

    [Fact]
    public void Down_from_main_enters_the_branch_below_without_moving_main()
    {
        var (m, c) = Pivot();
        Assert.True(m.Apply(NavAction.Dive)); // ↓ → into feat-2 (below)
        Assert.False(m.OnTop);
        Assert.Equal((1, 0), m.CurrentBranchDesktop);
        Assert.Equal(D(20), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition);
    }

    [Fact]
    public void Crossing_main_from_below_to_above_keeps_the_stack_stable()
    {
        var (m, _) = Pivot();
        m.GoToBranchDesktop(1, 0);                 // in feat-2 (below main)
        Assert.True(m.Apply(NavAction.Surface));  // feat-2 → main
        Assert.True(m.OnTop);
        Assert.True(m.Apply(NavAction.Surface));  // main → feat-1 (above)
        Assert.Equal((0, 0), m.CurrentBranchDesktop);
        Assert.Equal(1, m.BuildMap().TopPosition); // whole sequence unchanged throughout
    }

    [Fact]
    public void New_branch_appears_below_main_leaving_the_above_branches_in_place()
    {
        var (m, _) = Pivot(); // feat-1 above (slot 1), feat-2 below
        m.AddBranch(G("hotfix", G3));
        NavMap map = m.BuildMap();
        Assert.Equal(1, map.TopPosition); // still one branch above main
        Assert.Equal(new[] { "feat-1", "hotfix", "feat-2" }, map.Branches.Select(g => g.Name));
    }

    // ── AddBranchBelow: attach below a selection anchor, not always below main ────────

    [Fact]
    public void AddBranchBelow_a_main_selection_inserts_directly_below_main()
    {
        var (m, _) = Pivot(); // feat-1 above main (slot 1), feat-2 below
        m.AddBranchBelow(onMain: true, branchIndex: -1, G("hotfix", G3));
        NavMap map = m.BuildMap();
        Assert.Equal(1, map.TopPosition); // unchanged
        Assert.Equal(new[] { "feat-1", "hotfix", "feat-2" }, map.Branches.Select(g => g.Name));
    }

    [Fact]
    public void AddBranchBelow_a_below_main_branch_inserts_right_after_it()
    {
        var (m, _) = Pivot();
        m.AddBranchBelow(onMain: false, branchIndex: 1, G("hotfix", G3)); // below feat-2 (below main)
        NavMap map = m.BuildMap();
        Assert.Equal(1, map.TopPosition); // main slot unchanged — insertion was below main
        Assert.Equal(new[] { "feat-1", "feat-2", "hotfix" }, map.Branches.Select(g => g.Name));
    }

    [Fact]
    public void AddBranchBelow_an_above_main_branch_stays_above_and_sinks_main_a_slot()
    {
        var (m, _) = Pivot();
        m.AddBranchBelow(onMain: false, branchIndex: 0, G("hotfix", G3)); // below feat-1 (above main)
        NavMap map = m.BuildMap();
        Assert.Equal(2, map.TopPosition); // now two branches above main — main sank to keep place
        Assert.Equal(new[] { "feat-1", "hotfix", "feat-2" }, map.Branches.Select(g => g.Name));
    }

    // ── AddDesktopToBranch: "new desktop" lands in the row you're looking at ─────────

    [Fact]
    public void AddDesktopToBranch_appends_to_that_branch_and_keeps_it_off_main()
    {
        var (m, c) = Pivot();          // feat-1 = a,b,c (above main); feat-2 = x,y (below)
        int mainBefore = m.BuildMap().TopRow.Count;

        DesktopId id = c.Create("feat-2 · z"); // App creates the OS desktop, then records where it belongs
        Assert.Equal(2, m.AddDesktopToBranch(1, new DesktopRef(id, "z")));

        NavMap map = m.BuildMap();
        Assert.Equal(new[] { "x", "y", "z" }, map.Branches[1].Desktops.Select(d => d.Label));
        Assert.Equal(mainBefore, map.TopRow.Count); // claimed by a branch, so it never shows up on main
        Assert.Equal(1, map.TopPosition);           // structure otherwise untouched
    }

    [Fact]
    public void AddDesktopToBranch_does_not_switch_or_move_the_resume_point()
    {
        var (m, c) = Pivot();
        m.Apply(NavAction.Dive);       // ↓ into feat-2, on x
        c.Switches.Clear();

        DesktopId id = c.Create("feat-2 · z");
        m.AddDesktopToBranch(1, new DesktopRef(id, "z"));

        Assert.Empty(c.Switches);                        // creating never takes you there
        Assert.Equal((1, 0), m.CurrentBranchDesktop);    // still on x
    }

    [Fact]
    public void AddDesktopToBranch_rejects_a_branch_that_is_gone()
    {
        var (m, c) = Pivot();
        Assert.Null(m.AddDesktopToBranch(2, new DesktopRef(c.Create("orphan"), "z"))); // only 0 and 1 exist
    }

    // ── Click-to-navigate ────────────────────────────────────────────────────────

    [Fact]
    public void GoToTop_jumps_to_a_specific_main_desktop()
    {
        var (m, c) = New();
        m.AddBranch(G("feat", G1));
        m.Apply(NavAction.Dive);
        Assert.True(m.GoToTop(2));
        Assert.True(m.OnTop);
        Assert.Equal(T2, c.Current);
    }

    [Fact]
    public void GoToBranchDesktop_jumps_into_a_branch_and_desktop_without_moving_main()
    {
        var (m, c) = Pivot();
        Assert.True(m.GoToBranchDesktop(1, 1)); // feat-2, desktop y
        Assert.False(m.OnTop);
        Assert.Equal((1, 1), m.CurrentBranchDesktop);
        Assert.Equal(D(21), c.Current);
        Assert.Equal(1, m.BuildMap().TopPosition);
    }

    // ── Branch removal ──────────────────────────────────────────────────────────

    [Fact]
    public void RemoveBranch_returns_it_and_surfaces_when_none_remain()
    {
        var (m, _) = New();
        m.AddBranch(G("feat", G1));
        m.Apply(NavAction.Dive);
        Branch? removed = m.RemoveBranch(0);
        Assert.Equal("feat", removed!.Name);
        Assert.Equal(0, m.BranchCount);
        Assert.True(m.OnTop);
        Assert.False(m.Apply(NavAction.Dive)); // nothing to dive into now
    }

    [Fact]
    public void Removing_a_branch_above_main_raises_the_main_slot_to_match()
    {
        var (m, _) = Pivot();      // feat-1 above main (slot 1), feat-2 below
        m.RemoveBranch(0);          // drop feat-1 (above) → main rises to slot 0
        NavMap map = m.BuildMap();
        Assert.Equal(0, map.TopPosition);
        Assert.Equal(new[] { "feat-2" }, map.Branches.Select(g => g.Name));
    }

    // ── Re-anchoring after a switch made outside Hypertree ───────────────────────

    [Fact]
    public void AnchorToCurrent_homes_the_cursor_onto_an_externally_switched_desktop()
    {
        var (m, c) = New();          // three main desktops, cursor on T0
        c.JumpExternally(T2);        // another app jumped us to T2 behind our back
        Assert.True(m.AnchorToCurrent());
        Assert.True(m.OnTop);
        Assert.Equal(2, m.CurrentTopIndex);
        Assert.Empty(c.Switches);    // re-anchoring never moves the user
    }

    [Fact]
    public void Navigating_after_an_external_jump_moves_from_where_you_actually_are()
    {
        var (m, c) = New(current: 0);
        c.JumpExternally(T1);        // externally moved to the middle desktop
        m.AnchorToCurrent();
        m.Apply(NavAction.MoveRight);
        Assert.Equal(new[] { T2 }, c.Switches); // one step right of T1, not of the stale T0
    }

    [Fact]
    public void AnchorToCurrent_homes_onto_a_branch_desktop_and_remembers_it_as_last_used()
    {
        var (m, c) = Pivot();        // feat-1: a,b,c (10,11,12) above main; feat-2 below
        c.JumpExternally(D(12));     // externally jumped to feat-1's "c"
        Assert.True(m.AnchorToCurrent());
        Assert.False(m.OnTop);
        Assert.Equal((0, 2), m.CurrentBranchDesktop);
    }

    [Fact]
    public void AnchorToCurrent_is_a_no_op_when_the_cursor_is_already_there()
    {
        var (m, _) = New();
        Assert.False(m.AnchorToCurrent());
        Assert.Equal(0, m.CurrentTopIndex);
    }

    [Fact]
    public void Reconcile_re_anchors_onto_an_externally_switched_desktop()
    {
        var (m, c) = New();          // the map/palette path — Reconcile precedes every surface
        c.JumpExternally(T2);
        m.Reconcile();
        Assert.True(m.BuildMap().TopRow[2].IsCurrent);
    }

    // ── Reconcile against externally-deleted desktops ───────────────────────────

    [Fact]
    public void Reconcile_drops_branch_desktops_the_os_no_longer_has()
    {
        var (m, c) = Pivot(); // feat-1: a,b,c (10,11,12); feat-2: x,y
        c.Remove(D(11), T0);  // user deletes feat-1's "b" from Task View
        m.Reconcile();
        var g = m.BuildMap().Branches.First(x => x.Name == "feat-1");
        Assert.Equal(new[] { "a", "c" }, g.Desktops.Select(t => t.Label));
    }

    [Fact]
    public void Reconcile_removes_a_branch_whose_desktops_all_vanish()
    {
        var (m, c) = Pivot();
        c.Remove(D(20), T0);
        c.Remove(D(21), T0); // feat-2 entirely gone from the OS
        m.Reconcile();
        Assert.Equal(new[] { "feat-1" }, m.BuildMap().Branches.Select(g => g.Name));
    }

    // ── Single-desktop deletion ──────────────────────────────────────────────────

    [Fact]
    public void DetachBranchDesktop_removes_one_desktop_and_keeps_the_branch()
    {
        var (m, _) = New();
        m.AddBranch(G("feat", G1)); // a, b, c
        DesktopId? id = m.DetachBranchDesktop(0, 1); // remove b
        Assert.Equal(D(11), id);
        Assert.Equal(1, m.BranchCount);
        Assert.Equal(new[] { "a", "c" }, m.BuildMap().Branches[0].Desktops.Select(t => t.Label));
    }

    [Fact]
    public void DetachBranchDesktop_removes_the_branch_when_its_last_desktop_goes()
    {
        var (m, _) = New();
        m.AddBranch(G("solo", (30, "only")));
        DesktopId? id = m.DetachBranchDesktop(0, 0);
        Assert.Equal(D(30), id);
        Assert.Equal(0, m.BranchCount);
    }

    [Fact]
    public void PeekTopDesktop_exposes_id_and_label_for_a_confirm_prompt()
    {
        var (m, _) = New();
        var peek = m.PeekTopDesktop(1);
        Assert.NotNull(peek);
        Assert.Equal(T1, peek!.Value.id);
    }

    [Fact]
    public void Restore_without_a_persisted_slot_puts_main_first_even_when_a_branch_is_active()
    {
        // Old state (or a fresh install) never recorded MainSlot. Even with a non-main active branch,
        // main must default to first (slot 0) rather than drifting down to follow the cursor's branch.
        var ids = new[] { T0, T1, T2, D(10), D(11), D(12), D(20), D(21) };
        var state = new PersistedState
        {
            MainSlot = null, ActiveBranch = 1, // cursor was inside the second branch
            Branches =
            {
                new PersistedBranch { Name = "feat-1", Desktops = { PD(10, "a"), PD(11, "b"), PD(12, "c") } },
                new PersistedBranch { Name = "feat-2", Desktops = { PD(20, "x"), PD(21, "y") } },
            },
        };
        var m = new NavigationModel(new FakeDesktopController(ids, 0), new InMemoryStore(state));

        Assert.Equal(0, m.BuildMap().TopPosition);              // main sits first, above both branches
        Assert.True(m.BuildStatus().Rows[0].IsMain);
    }

    [Fact]
    public void Restore_honours_an_explicit_zero_slot_instead_of_re_deriving_it()
    {
        // MainSlot == 0 is a real arrangement (main deliberately first), not "unset": it must be honoured
        // as-is and never fall back to the active branch. Regression: a stored 0 used to be discarded.
        var ids = new[] { T0, T1, T2, D(10), D(11), D(12), D(20), D(21) };
        var state = new PersistedState
        {
            MainSlot = 0, ActiveBranch = 1,
            Branches =
            {
                new PersistedBranch { Name = "feat-1", Desktops = { PD(10, "a"), PD(11, "b"), PD(12, "c") } },
                new PersistedBranch { Name = "feat-2", Desktops = { PD(20, "x"), PD(21, "y") } },
            },
        };
        var m = new NavigationModel(new FakeDesktopController(ids, 0), new InMemoryStore(state));

        Assert.Equal(0, m.BuildMap().TopPosition);
    }

    // ── Snapshots (capture / restore a whole named layout) ───────────────────────

    [Fact]
    public void CaptureSnapshot_records_the_main_slot_main_desktops_and_branches()
    {
        var (m, _) = Pivot(); // feat-1 above main (slot 1), feat-2 below; main = T0,T1,T2
        Snapshot snap = m.CaptureSnapshot("layout-1");

        Assert.Equal("layout-1", snap.Name);
        Assert.Equal(1, snap.MainSlot);
        Assert.Equal(8, snap.DesktopCount); // 3 main + 3 + 2
        Assert.Equal(new[] { T0.Value, T1.Value, T2.Value }, snap.MainDesktops.Select(d => d.Id));
        Assert.Equal(new[] { "feat-1", "feat-2" }, snap.Branches.Select(g => g.Name));
        Assert.Equal(new[] { D(10).Value, D(11).Value, D(12).Value }, snap.Branches[0].Desktops.Select(d => d.Id));
        Assert.Equal(new[] { "a", "b", "c" }, snap.Branches[0].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void RestoreStructure_replaces_branches_and_rederives_the_main_timeline()
    {
        var (_, c) = Pivot();
        // A fresh model over the same OS (no persisted branches) sees all 8 desktops as unbranched.
        var fresh = new NavigationModel(c);
        Assert.Equal(8, fresh.BuildMap().TopRow.Count);
        Assert.Empty(fresh.BuildMap().Branches);

        fresh.RestoreStructure(1, new[] { G("feat-1", G1), G("feat-2", G2) });

        NavMap map = fresh.BuildMap();
        Assert.Equal(3, map.TopRow.Count);  // only T0,T1,T2 stay on the main timeline now
        Assert.Equal(1, map.TopPosition);   // the restored main slot is honoured
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Branches.Select(g => g.Name));
    }

    [Fact]
    public void Snapshot_round_trips_through_capture_and_restore()
    {
        var (source, c) = Pivot();
        Snapshot snap = source.CaptureSnapshot("s");

        var target = new NavigationModel(c); // same OS, no persisted branches
        var branches = snap.Branches
            .Select(g => new Branch(g.Name,
                g.Desktops.Select(d => new DesktopRef(new DesktopId(d.Id), d.Label)).ToList(),
                g.LastUsedIndex))
            .ToList();
        target.RestoreStructure(snap.MainSlot, branches);

        NavMap a = source.BuildMap(), b = target.BuildMap();
        Assert.Equal(a.TopPosition, b.TopPosition);
        Assert.Equal(a.TopRow.Count, b.TopRow.Count);
        Assert.Equal(a.Branches.Select(g => g.Name), b.Branches.Select(g => g.Name));
        Assert.Equal(
            a.Branches.SelectMany(g => g.Desktops.Select(d => d.Label)),
            b.Branches.SelectMany(g => g.Desktops.Select(d => d.Label)));
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

        NavMapBranch feat1 = map.Branches.First(g => g.Name == "feat-1");
        Assert.Equal(2, feat1.Desktops[0].WindowCount);
        Assert.Equal(0, feat1.Desktops[1].WindowCount);
    }

    // ── "Came from" green marker during navigation ───────────────────────────────

    [Fact]
    public void BuildMap_marks_the_came_from_desktop_as_here()
    {
        var (m, _) = New(current: 0); // on T0
        m.Apply(NavAction.MoveRight); // now on T1, having come from T0

        NavMap map = m.BuildMap(T0);
        Assert.True(map.TopRow[0].IsHere);    // T0 (came from) → green
        Assert.False(map.TopRow[0].IsCurrent);
        Assert.True(map.TopRow[1].IsCurrent); // T1 (now) → blue
        Assert.False(map.TopRow[1].IsHere);
    }

    [Fact]
    public void BuildMap_without_a_came_from_marks_nothing_here()
    {
        var (m, _) = New();
        Assert.All(m.BuildMap().TopRow, t => Assert.False(t.IsHere));
    }
}
