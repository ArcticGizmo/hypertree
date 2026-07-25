using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers persistence + restore: branches survive a restart, and desktops that no longer exist are
/// dropped rather than resurrected — so Hypertree's created desktops aren't orphaned into the top row.
/// </summary>
public class PersistenceTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private sealed class InMemoryStore : IStateStore
    {
        public PersistedState State = new();
        public PersistedState Load() => State;
        public void Save(PersistedState s) => State = s;
    }

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    [Fact]
    public void A_branch_survives_a_restart_when_its_desktops_still_exist()
    {
        var store = new InMemoryStore();
        // OS lists the top desktops AND the branch's desktops.
        var ids = new[] { D(0), D(1), D(10), D(11) };

        var m1 = new NavigationModel(new FakeDesktopController(ids, 0), store);
        m1.AddBranch(G("feat", (10, "a"), (11, "b")));

        // Restart: fresh model, same store, same OS desktops.
        var m2 = new NavigationModel(new FakeDesktopController(ids, 0), store);
        Assert.Equal(1, m2.BranchCount);
        NavMap map = m2.BuildMap();
        Assert.Equal(2, map.TopRow.Count);              // D(10)/D(11) are NOT orphaned into the top row
        Assert.Equal("feat", map.Branches[0].Name);
        Assert.Equal(new[] { "a", "b" }, map.Branches[0].Desktops.Select(t => t.Label));
    }

    [Fact]
    public void Vanished_branch_desktops_are_dropped_on_restore()
    {
        var store = new InMemoryStore();
        new NavigationModel(new FakeDesktopController(new[] { D(0), D(10), D(11) }, 0), store)
            .AddBranch(G("feat", (10, "a"), (11, "b")));

        // Restart with those branch desktops gone from the OS entirely.
        var m = new NavigationModel(new FakeDesktopController(new[] { D(0), D(1) }, 0), store);
        Assert.Equal(0, m.BranchCount); // whole branch dropped — nothing to orphan
    }

    [Fact]
    public void Partially_surviving_branch_keeps_only_the_live_desktops()
    {
        var store = new InMemoryStore();
        new NavigationModel(new FakeDesktopController(new[] { D(0), D(10), D(11) }, 0), store)
            .AddBranch(G("feat", (10, "a"), (11, "b")));

        // Only D(10) survives.
        var m = new NavigationModel(new FakeDesktopController(new[] { D(0), D(10) }, 0), store);
        Assert.Equal(1, m.BranchCount);
        Assert.Single(m.BuildMap().Branches[0].Desktops);
    }
}
