using System.Text.Json;
using Hypertree.Store;
using Hypertree.WindowLayout;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the OS-free half of monitor-layout restore: the set-identity key, the JSON round-trip the store
/// relies on, and <see cref="MonitorLayoutService"/>'s leave-saves / arrive-offers decision logic with its
/// two-tick debounce — all against a fake controller, no OS involved.
/// </summary>
public class MonitorLayoutTests
{
    // ── MonitorSet.Key ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set_key_is_independent_of_monitor_order()
    {
        var a = new[] { Mon("A"), Mon("B"), Mon("C") };
        var b = new[] { Mon("C"), Mon("A"), Mon("B") };
        Assert.Equal(MonitorSet.Key(a), MonitorSet.Key(b));
    }

    [Fact]
    public void Set_key_changes_when_a_monitor_joins_or_leaves()
    {
        string three = MonitorSet.Key(new[] { Mon("A"), Mon("B"), Mon("C") });
        string two = MonitorSet.Key(new[] { Mon("A"), Mon("B") });
        Assert.NotEqual(three, two);
    }

    [Fact]
    public void Set_key_ignores_bounds_and_friendly_name_using_only_stable_id()
    {
        // The same physical monitor at a different resolution / position after a dock is still the same set.
        var docked = new[] { new MonitorRef("A", "Dell", new Recti(0, 0, 2560, 1440), true, 120) };
        var moved = new[] { new MonitorRef("A", "renamed", new Recti(-1920, 87, 1920, 1080), false, 96) };
        Assert.Equal(MonitorSet.Key(docked), MonitorSet.Key(moved));
    }

    // ── Store JSON round-trip (records with IReadOnlyList / record struct) ─────────────────────────────

    [Fact]
    public void Layout_file_survives_a_json_round_trip()
    {
        var snap = new MonitorLayoutSnapshot("2m-abc",
            new[] { Mon("A"), Mon("B") },
            new[]
            {
                new WindowPlacement(0x1234, "A", "Editor", new Recti(10, 20, 800, 600), ShowState.Maximized),
                new WindowPlacement(0x5678, "B", "Browser", new Recti(-5, 0, 1200, 900), ShowState.Normal),
            });
        var file = new MonitorLayoutFile();
        file.Auto.Add(snap);
        file.Named.Add(new NamedMonitorLayout("office", snap));

        string json = JsonSerializer.Serialize(file);
        MonitorLayoutFile back = JsonSerializer.Deserialize<MonitorLayoutFile>(json)!;

        // Compare field-by-field: record Equals uses reference equality for the IReadOnlyList members, so
        // whole-record equality would fail on distinct-but-equal lists even when the round-trip is perfect.
        MonitorLayoutSnapshot a = back.Auto[0];
        Assert.Equal("2m-abc", a.SetKey);
        Assert.Equal(new[] { "A", "B" }, a.Monitors.Select(m => m.StableId));
        Assert.Equal(2, a.Windows.Count);
        Assert.Equal(0x1234, a.Windows[0].Hwnd);
        Assert.Equal(ShowState.Maximized, a.Windows[0].Show);
        Assert.Equal(new Recti(10, 20, 800, 600), a.Windows[0].NormalOffset);

        Assert.Equal("office", back.Named[0].Name);
        Assert.Equal(new Recti(-5, 0, 1200, 900), back.Named[0].Layout.Windows[1].NormalOffset);
    }

    // ── Service: leave-saves, arrive-offers, debounced ─────────────────────────────────────────────────

    [Fact]
    public void A_single_changed_tick_does_nothing_it_must_settle_first()
    {
        var c = Docked3();
        var store = new InMemoryMonitorLayoutStore();
        var svc = new MonitorLayoutService(c, store);

        c.Undock("A"); // now one monitor
        Assert.False(svc.Tick()); // first sighting — pending, no action
        Assert.Empty(store.Auto);
    }

    [Fact]
    public void Undock_saves_the_pre_undock_layout_under_the_set_being_left()
    {
        var c = Docked3(windows: 2);
        var store = new InMemoryMonitorLayoutStore();
        var svc = new MonitorLayoutService(c, store);
        string threeKey = svc.CurrentSetKey; // the dock we're about to leave

        c.Undock("A"); // shell crushes everything onto one screen
        svc.Tick();    // pending
        Assert.True(svc.Tick()); // settled → acts

        MonitorLayoutSnapshot? saved = store.GetAuto(threeKey);
        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Windows.Count); // the arrangement from before the crush, not after
    }

    [Fact]
    public void Redock_to_a_known_set_offers_the_saved_layout()
    {
        var c = Docked3(windows: 3);
        var store = new InMemoryMonitorLayoutStore();
        var svc = new MonitorLayoutService(c, store);
        string threeKey = svc.CurrentSetKey;

        var offered = new List<MonitorLayoutSnapshot>();
        svc.RestoreAvailable += offered.Add;

        // undock (saves the 3-set), then redock
        c.Undock("A"); svc.Tick(); svc.Tick();
        Assert.Empty(offered); // leaving doesn't offer

        c.Redock3(); svc.Tick(); svc.Tick();

        Assert.Single(offered);
        Assert.Equal(threeKey, offered[0].SetKey);
        Assert.Equal(3, offered[0].Windows.Count);
    }

    [Fact]
    public void Returning_to_a_single_screen_never_offers_even_if_a_layout_is_saved()
    {
        // Everything is forced onto the one monitor anyway, so there's nothing to put back — no prompt.
        var c = Docked3();
        var store = new InMemoryMonitorLayoutStore();
        string oneKey = MonitorSet.Key(new[] { Mon("A") });
        store.PutAuto(new MonitorLayoutSnapshot(oneKey, new[] { Mon("A") },
            new[] { new WindowPlacement(1, "A", "w", new Recti(0, 0, 1, 1), ShowState.Normal) }));
        var svc = new MonitorLayoutService(c, store);

        var offered = new List<MonitorLayoutSnapshot>();
        svc.RestoreAvailable += offered.Add;

        c.Undock("A"); svc.Tick(); svc.Tick();
        Assert.Empty(offered);
    }

    [Fact]
    public void Arriving_at_an_unknown_set_offers_nothing()
    {
        var c = Docked3();
        var store = new InMemoryMonitorLayoutStore();
        var svc = new MonitorLayoutService(c, store);

        var offered = new List<MonitorLayoutSnapshot>();
        svc.RestoreAvailable += offered.Add;

        c.Undock("A"); svc.Tick(); svc.Tick(); // never seen a 1-monitor set before
        Assert.Empty(offered);
    }

    [Fact]
    public void Restore_routes_to_the_controller_and_reports_what_it_placed()
    {
        var c = Docked3(windows: 4);
        var svc = new MonitorLayoutService(c, new InMemoryMonitorLayoutStore());
        MonitorLayoutSnapshot snap = svc.CaptureNow();

        RestoreReport r = svc.Restore(snap);

        Assert.Single(c.Restored);
        Assert.Equal(4, r.Placed);
    }

    [Fact]
    public void A_named_save_captures_the_current_arrangement()
    {
        var c = Docked3(windows: 2);
        var store = new InMemoryMonitorLayoutStore();
        var svc = new MonitorLayoutService(c, store);

        svc.SaveNamed("office");

        Assert.Single(store.Named());
        Assert.Equal("office", store.Named()[0].Name);
        Assert.Equal(2, store.Named()[0].Layout.Windows.Count);

        svc.DeleteNamed("office");
        Assert.Empty(store.Named());
    }

    // ── PlanRestore: what a restore would actually move, and whether it needs the curtain ──────────────

    [Fact]
    public void Plan_counts_only_windows_on_the_wrong_monitor()
    {
        Recti r = new(0, 0, 800, 600);
        var c = new FakeWindowLayoutController();
        c.SetMonitors("A", "B", "C");
        c.Windows.Add(new WindowPlacement(1, "A", "drifted", r, ShowState.Normal));  // wants B → moves
        c.Windows.Add(new WindowPlacement(2, "B", "in place", r, ShowState.Normal)); // wants B → stays
        var svc = new MonitorLayoutService(c, new InMemoryMonitorLayoutStore());

        var reference = new MonitorLayoutSnapshot(MonitorSet.Key(c.Monitors()), c.Monitors().ToList(), new[]
        {
            new WindowPlacement(1, "B", "drifted", r, ShowState.Normal),
            new WindowPlacement(2, "B", "in place", r, ShowState.Normal),
        });

        RestorePlan plan = svc.PlanRestore(reference);
        Assert.Equal(1, plan.ToMove);
        Assert.False(plan.NeedsCurtain); // the mover is Normal, not Maximized
    }

    [Fact]
    public void Plan_needs_the_curtain_only_when_a_mover_is_maximized()
    {
        Recti r = new(0, 0, 800, 600);
        var c = new FakeWindowLayoutController();
        c.SetMonitors("A", "B");
        c.Windows.Add(new WindowPlacement(1, "A", "slack", r, ShowState.Maximized)); // wants B, maximized → curtain
        var svc = new MonitorLayoutService(c, new InMemoryMonitorLayoutStore());

        var reference = new MonitorLayoutSnapshot(MonitorSet.Key(c.Monitors()), c.Monitors().ToList(),
            new[] { new WindowPlacement(1, "B", "slack", r, ShowState.Maximized) });

        RestorePlan plan = svc.PlanRestore(reference);
        Assert.Equal(1, plan.ToMove);
        Assert.True(plan.NeedsCurtain);
    }

    [Fact]
    public void Plan_ignores_closed_windows_and_absent_target_monitors()
    {
        Recti r = new(0, 0, 800, 600);
        var c = new FakeWindowLayoutController();
        c.SetMonitors("A", "B"); // C is not present
        c.Windows.Add(new WindowPlacement(1, "A", "wants absent C", r, ShowState.Maximized));
        var svc = new MonitorLayoutService(c, new InMemoryMonitorLayoutStore());

        var reference = new MonitorLayoutSnapshot(MonitorSet.Key(c.Monitors()), c.Monitors().ToList(), new[]
        {
            new WindowPlacement(1, "C", "wants absent C", r, ShowState.Maximized), // target monitor absent → skip
            new WindowPlacement(9, "B", "closed since capture", r, ShowState.Normal), // not open now → skip
        });

        RestorePlan plan = svc.PlanRestore(reference);
        Assert.Equal(0, plan.ToMove);
        Assert.False(plan.NeedsCurtain);
    }

    // ── fakes ──────────────────────────────────────────────────────────────────────────────────────────

    private static MonitorRef Mon(string id) =>
        new(id, id, new Recti(0, 0, 1920, 1080), IsPrimary: id == "A", Dpi: 96);

    private static FakeWindowLayoutController Docked3(int windows = 0)
    {
        var c = new FakeWindowLayoutController();
        c.SetMonitors("A", "B", "C");
        for (int i = 0; i < windows; i++)
            c.Windows.Add(new WindowPlacement(0x100 + i, "A", $"w{i}", new Recti(i * 10, 0, 800, 600), ShowState.Normal));
        return c;
    }

    /// <summary>An in-memory <see cref="IWindowLayoutController"/>: a settable monitor set and window list,
    /// recording every <see cref="Restore"/>. <see cref="Snapshot"/> always reflects the current monitors,
    /// so its <c>SetKey</c> tracks docks the way the real controller's does.</summary>
    private sealed class FakeWindowLayoutController : IWindowLayoutController
    {
        public List<MonitorRef> Mons = new();
        public List<WindowPlacement> Windows = new();
        public List<MonitorLayoutSnapshot> Restored = new();

        public void SetMonitors(params string[] ids) =>
            Mons = ids.Select((id, i) => new MonitorRef(id, id, new Recti(i * 1920, 0, 1920, 1080), i == 0, 96)).ToList();

        public void Undock(string keepId)
        {
            SetMonitors(keepId);
            // the shell would have re-homed windows; the exact list doesn't matter to these tests
            foreach (WindowPlacement w in Windows.ToList())
                Windows[Windows.IndexOf(w)] = w with { MonitorStableId = keepId };
        }

        public void Redock3() => SetMonitors("A", "B", "C");

        public IReadOnlyList<MonitorRef> Monitors() => Mons;
        public MonitorLayoutSnapshot Snapshot() =>
            new(MonitorSet.Key(Mons), Mons.ToList(), Windows.ToList());
        public RestoreReport Restore(MonitorLayoutSnapshot s)
        {
            Restored.Add(s);
            return new RestoreReport(s.Windows.Count, 0, 0, 0);
        }

        // Diagnostics aren't exercised by these tests — the fake satisfies the interface only.
        public IReadOnlyList<WindowRestoreTrace> RestoreTraced(MonitorLayoutSnapshot s)
        {
            Restored.Add(s);
            return Array.Empty<WindowRestoreTrace>();
        }
        public WindowProbe Probe(long hwnd) => new(false, default, "", "", ShowState.Normal);
    }

    private sealed class InMemoryMonitorLayoutStore : IMonitorLayoutStore
    {
        public List<MonitorLayoutSnapshot> Auto = new();
        private readonly List<NamedMonitorLayout> _named = new();

        public MonitorLayoutSnapshot? GetAuto(string setKey) => Auto.FirstOrDefault(s => s.SetKey == setKey);
        public void PutAuto(MonitorLayoutSnapshot s) { Auto.RemoveAll(x => x.SetKey == s.SetKey); Auto.Add(s); }
        public IReadOnlyList<NamedMonitorLayout> Named() => _named;
        public void SaveNamed(NamedMonitorLayout l)
        {
            _named.RemoveAll(n => n.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
            _named.Add(l);
        }
        public void DeleteNamed(string name) => _named.RemoveAll(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
