using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Rearranging the layout from the map: re-slotting a branch in the vertical stack (Shift+↑↓ / a dragged
/// branch box) and moving a single desktop along its row, between branches, or on/off the main timeline
/// (Ctrl+arrows / a dragged tile). Both are pure structure — nothing is created, destroyed or switched —
/// with two consequences worth pinning down: a branch stepping across main just re-slots main, and landing
/// a desktop on main has to reorder the <em>OS</em> list, because the main timeline is that order.
/// </summary>
public class RearrangeTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId T0 = D(0), T1 = D(1), T2 = D(2);

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private sealed class InMemoryStore : IStateStore
    {
        public PersistedState State = new();
        public PersistedState Load() => State;
        public void Save(PersistedState s) => State = s;
    }

    private static PersistedDesktop PD(int id, string label) => new() { Id = D(id).Value, Label = label };

    // Main between two branches (mainSlot 1): rows read feat-1 / main / feat-2. Every desktop — main's and
    // both branches' — is a live OS desktop, so a desktop dragged out of a branch really does reappear on
    // the main timeline, and the OS ordinals a main-timeline drop anchors against are real.
    private static (NavigationModel m, FakeDesktopController c, InMemoryStore s) Pivot(int firstBranchCursor = 0)
    {
        var ids = new[] { T0, T1, T2, D(10), D(11), D(12), D(20), D(21) };
        var store = new InMemoryStore
        {
            State = new PersistedState
            {
                MainSlot = 1, ActiveBranch = 0,
                Branches =
                {
                    new PersistedBranch
                    {
                        Name = "feat-1", LastUsedIndex = firstBranchCursor,
                        Desktops = { PD(10, "a"), PD(11, "b"), PD(12, "c") },
                    },
                    new PersistedBranch { Name = "feat-2", Desktops = { PD(20, "x"), PD(21, "y") } },
                },
            },
        };
        var c = new FakeDesktopController(ids, 0); // OS current = T0, a main-timeline desktop
        return (new NavigationModel(c, store), c, store);
    }

    // Main on top with three branches below it, nearest first: [A, B, C].
    private static (NavigationModel m, FakeDesktopController c) Stack()
    {
        var c = new FakeDesktopController(new[] { T0, T1, T2 }, 0);
        var m = new NavigationModel(c);
        m.AddBranch(G("C", (30, "p")));
        m.AddBranch(G("B", (20, "x")));
        m.AddBranch(G("A", (10, "a"))); // each lands directly below main
        return (m, c);
    }

    private static IEnumerable<string> Names(NavigationModel m) => m.Map().Branches.Select(g => g.Name);

    // ── Re-slotting a branch ──────────────────────────────────────────────────────

    [Fact]
    public void Moving_a_branch_down_a_row_swaps_it_with_the_branch_below()
    {
        var (m, c) = Stack(); // main / A / B / C — main at row 0, A at row 1
        Assert.Equal(1, m.MoveBranchToRow(0, 2));

        Assert.Equal(new[] { "B", "A", "C" }, Names(m));
        Assert.Equal(0, m.Map().TopPosition); // main stays on top
        Assert.Empty(c.Switches);                  // rearranging never switches desktop
    }

    [Fact]
    public void A_branch_moved_onto_mains_row_crosses_main_and_re_slots_it()
    {
        var (m, _) = Stack(); // main / A / B / C
        Assert.Equal(0, m.MoveBranchToRow(0, 0)); // A takes main's row

        var map =m.Map();
        Assert.Equal(new[] { "A", "B", "C" }, map.Branches.Select(g => g.Name)); // order untouched…
        Assert.Equal(1, map.TopPosition);                                        // …but A now renders above main
    }

    [Fact]
    public void A_branch_above_main_moved_down_drops_below_it()
    {
        var (m, _, _) = Pivot(); // feat-1 / main / feat-2 (mainSlot 1)
        Assert.Equal(0, m.MoveBranchToRow(0, 1)); // feat-1 onto main's row

        var map =m.Map();
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Branches.Select(g => g.Name));
        Assert.Equal(0, map.TopPosition); // nothing above main any more
    }

    [Fact]
    public void Moving_a_branch_to_the_row_it_already_holds_is_a_noop()
    {
        var (m, _, _) = Pivot();
        Assert.Null(m.MoveBranchToRow(0, 0));  // feat-1 is already the top row
        Assert.Null(m.MoveBranchToRow(0, -3)); // clamps onto row 0 — still a no-op
        Assert.Null(m.MoveBranchToRow(7, 0));  // no such branch
    }

    [Fact]
    public void The_cursor_stays_inside_a_branch_that_is_re_slotted()
    {
        var (m, c, _) = Pivot();
        m.GoToBranchDesktop(1, 1); // inside feat-2, on "y"
        c.Switches.Clear();

        Assert.Equal(0, m.MoveBranchToRow(1, 0)); // carry feat-2 to the top of the stack
        Assert.Equal((0, 1), m.CurrentBranchDesktop);
        Assert.Empty(c.Switches); // still standing on the same desktop
    }

    [Fact]
    public void A_re_slotted_stack_is_persisted()
    {
        var (m, _, store) = Pivot();
        m.MoveBranchToRow(1, 0); // feat-2 to the top: [feat-2, feat-1], both above main

        Assert.Equal(new[] { "feat-2", "feat-1" }, store.State.Branches.Select(b => b.Name));
        Assert.Equal(2, store.State.MainSlot);
    }

    // ── Re-slotting main itself ───────────────────────────────────────────────────

    [Fact]
    public void Moving_main_down_a_row_sinks_it_below_the_branch_below()
    {
        var (m, c) = Stack(); // main / A / B / C — main at row 0
        Assert.Equal(1, m.MoveMainToRow(1)); // main steps past A

        var map =m.Map();
        Assert.Equal(new[] { "A", "B", "C" }, map.Branches.Select(g => g.Name)); // branches untouched
        Assert.Equal(1, map.TopPosition);                                        // A now renders above main
        Assert.Empty(c.Switches);                                                // re-slotting never switches
    }

    [Fact]
    public void Moving_main_up_lifts_it_above_the_branch_on_top()
    {
        var (m, _, _) = Pivot(); // feat-1 / main / feat-2 (mainSlot 1)
        Assert.Equal(0, m.MoveMainToRow(0)); // main to the top of the stack

        var map =m.Map();
        Assert.Equal(new[] { "feat-1", "feat-2" }, map.Branches.Select(g => g.Name));
        Assert.Equal(0, map.TopPosition); // nothing above main any more
    }

    [Fact]
    public void Moving_main_to_the_row_it_already_holds_is_a_noop()
    {
        var (m, _) = Stack(); // main at row 0
        Assert.Null(m.MoveMainToRow(0));  // already the top row
        Assert.Null(m.MoveMainToRow(-2)); // clamps onto row 0 — still a no-op
    }

    [Fact]
    public void A_re_slotted_main_is_persisted()
    {
        var (m, _, store) = Pivot(); // feat-1 / main / feat-2 (mainSlot 1)
        m.MoveMainToRow(2);          // main to the bottom: feat-1 / feat-2 / main

        Assert.Equal(new[] { "feat-1", "feat-2" }, store.State.Branches.Select(b => b.Name));
        Assert.Equal(2, store.State.MainSlot);
    }

    // ── Moving a desktop ──────────────────────────────────────────────────────────

    [Fact]
    public void A_desktop_moves_along_its_own_branch()
    {
        var (m, c, _) = Pivot();
        // "a" past "b": the insertion point counts the desktop itself, so index 2 is one place right.
        Assert.Equal(new DesktopAddress(false, 0, 1), m.MoveDesktop(new(false, 0, 0), new(false, 0, 2)));

        Assert.Equal(new[] { "b", "a", "c" }, m.Map().Branches[0].Desktops.Select(d => d.Label));
        Assert.Empty(c.Switches);
    }

    [Fact]
    public void A_desktop_moves_into_another_branch_at_the_drop_position()
    {
        var (m, _, _) = Pivot();
        Assert.Equal(new DesktopAddress(false, 1, 1), m.MoveDesktop(new(false, 0, 1), new(false, 1, 1))); // feat-1's "b" between x and y

        var map =m.Map();
        Assert.Equal(new[] { "a", "c" }, map.Branches[0].Desktops.Select(d => d.Label));
        Assert.Equal(new[] { "x", "b", "y" }, map.Branches[1].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void A_desktop_dragged_out_of_a_branch_rejoins_main_where_it_was_dropped()
    {
        var (m, c, _) = Pivot();
        // feat-1's "b" (D(11)) onto main, between T0 and T1.
        Assert.Equal(new DesktopAddress(true, -1, 1), m.MoveDesktop(new(false, 0, 1), new(true, -1, 1)));

        Assert.Equal(new[] { T0, D(11), T1, T2 }, MainIds(m));
        Assert.Equal(new[] { "a", "c" }, m.Map().Branches[0].Desktops.Select(d => d.Label));
        Assert.Equal((D(11), 1), c.Reorders.Single()); // main is the OS order, so the OS had to reorder
        Assert.Empty(c.Switches);
    }

    [Fact]
    public void A_main_desktop_moves_into_a_branch_and_leaves_the_timeline()
    {
        var (m, _, _) = Pivot();
        Assert.Equal(new DesktopAddress(false, 1, 2), m.MoveDesktop(new(true, -1, 1), new(false, 1, 2))); // T1 onto the end of feat-2

        Assert.Equal(new[] { T0, T2 }, MainIds(m));
        Assert.Equal(new[] { "x", "y", "d1" }, m.Map().Branches[1].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void A_desktop_reordered_within_main_moves_in_the_os_list()
    {
        var (m, c, _) = Pivot();
        Assert.Equal(new DesktopAddress(true, -1, 1), m.MoveDesktop(new(true, -1, 0), new(true, -1, 2))); // T0 between T1 and T2

        Assert.Equal(new[] { T1, T0, T2 }, MainIds(m));
        Assert.Equal((T0, 1), c.Reorders.Single());
    }

    [Fact]
    public void Taking_a_branchs_last_desktop_dissolves_the_branch()
    {
        var c = new FakeDesktopController(new[] { T0, T1, T2, D(30) }, 0);
        var m = new NavigationModel(c);
        m.AddBranch(G("solo", (30, "s")));

        Assert.Equal(new DesktopAddress(true, -1, 3), m.MoveDesktop(new(false, 0, 0), new(true, -1, 3)));
        Assert.Equal(0, m.BranchCount);
        Assert.Equal(new[] { T0, T1, T2, D(30) }, MainIds(m));
    }

    [Fact]
    public void Dissolving_a_branch_keeps_the_stack_below_it_addressable()
    {
        var (m, _) = Stack(); // main / A(a) / B(x) / C(p) — one desktop each
        // A's only desktop into C: A dissolves, so C's index shifts up under the move.
        Assert.Equal(new DesktopAddress(false, 1, 1), m.MoveDesktop(new(false, 0, 0), new(false, 2, 1)));

        var map =m.Map();
        Assert.Equal(new[] { "B", "C" }, map.Branches.Select(g => g.Name));
        Assert.Equal(new[] { "p", "a" }, map.Branches[1].Desktops.Select(d => d.Label));
    }

    [Fact]
    public void An_inserted_desktop_leaves_the_branchs_resume_point_where_it_was()
    {
        var (m, _, _) = Pivot(firstBranchCursor: 2); // feat-1 resumes on "c"
        m.MoveDesktop(new(true, -1, 1), new(false, 0, 0));     // T1 in front of "a"

        var feat1 =m.Map().Branches[0];
        Assert.Equal(4, feat1.Desktops.Count);
        Assert.Equal(3, feat1.Cursor); // still "c", now one place along
    }

    [Fact]
    public void A_move_that_cannot_be_resolved_is_rejected()
    {
        var (m, c, _) = Pivot();
        Assert.Null(m.MoveDesktop(new(true, -1, 9), new(false, 0, 0)));  // no such main desktop
        Assert.Null(m.MoveDesktop(new(false, 5, 0), new(true, -1, 0)));  // no such branch
        Assert.Null(m.MoveDesktop(new(false, 0, 0), new(false, 0, 0)));  // already there
        Assert.Empty(c.Reorders);
    }

    [Fact]
    public void A_desktop_the_os_has_lost_cannot_rejoin_main()
    {
        var (m, c, _) = Pivot();
        c.Remove(D(11), T0); // deleted from Task View — the open map is still drawing a tile for it

        Assert.Null(m.MoveDesktop(new(false, 0, 1), new(true, -1, 0)));
        Assert.Equal(3, m.Map().Branches[0].Desktops.Count); // refused outright, branch untouched
        Assert.Empty(c.Reorders);
    }

    [Fact]
    public void The_only_desktop_on_main_has_nowhere_to_be_reordered_to()
    {
        var c = new FakeDesktopController(new[] { T0 }, 0);
        var m = new NavigationModel(c);
        Assert.Null(m.MoveDesktop(new(true, -1, 0), new(true, -1, 1)));
        Assert.Empty(c.Reorders);
    }

    private static DesktopId[] MainIds(NavigationModel m)
        => Enumerable.Range(0, m.Map().TopRow.Count)
                     .Select(i => m.PeekTopDesktop(i)!.Value.id)
                     .ToArray();
}
