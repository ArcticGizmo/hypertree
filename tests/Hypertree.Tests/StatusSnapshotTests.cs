using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Status;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers what Hypertree publishes to the outside world: the flattened row order (main in its slot rather
/// than a row the reader has to synthesise), where the cursor is, and the stable branch ids that
/// <c>htree goto</c> and Perch address rows by.
/// </summary>
public class StatusSnapshotTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private sealed class InMemoryStore : IStateStore
    {
        public PersistedState State = new();
        public PersistedState Load() => State;
        public void Save(PersistedState s) => State = s;
    }

    [Fact]
    public void Main_appears_as_a_row_in_its_slot()
    {
        var desktops = new FakeDesktopController(new[] { D(0), D(1), D(10), D(20) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("below", (10, "b1")));   // lands directly below main
        model.MoveBranchToRow(0, 0);               // …then move it above main

        StatusSnapshot status = model.BuildStatus();

        // A reader renders this array as it stands — no mainSlot arithmetic, no synthesised main row.
        Assert.Equal(new[] { "below", "main" }, status.Rows.Select(r => r.Name));
        Assert.Equal(new[] { RowKind.Branch, RowKind.Main }, status.Rows.Select(r => r.Kind));
        Assert.True(status.Rows[1].IsMain);
        Assert.Null(status.Rows[1].Id); // main has no identity to carry
    }

    [Fact]
    public void The_current_row_index_accounts_for_main_sitting_between_branches()
    {
        var desktops = new FakeDesktopController(new[] { D(0), D(10), D(20) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("first", (10, "a")));
        model.AddBranch(G("second", (20, "b"))); // also inserted below main

        // Put one branch above main so the cursor's branch index and its row index genuinely differ.
        model.MoveBranchToRow(0, 0);
        StatusSnapshot before = model.BuildStatus();
        int mainRow = before.Rows.FindIndex(r => r.IsMain);

        // Jump into the branch below main, then check the published row index points back at it.
        int below = before.Rows.FindIndex(r => !r.IsMain && before.Rows.IndexOf(r) > mainRow);
        Guid id = before.Rows[below].Id!.Value;
        Assert.Equal(GoToResult.Ok, model.GoTo(id, null, out _));

        StatusSnapshot after = model.BuildStatus();
        Assert.Equal(below, after.Current.Row);
        Assert.Equal(id, after.CurrentRow!.Id);
    }

    [Fact]
    public void The_published_cursor_follows_a_switch_made_outside_hypertree()
    {
        var desktops = new FakeDesktopController(new[] { D(0), D(1), D(10) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("work", (10, "code")));

        desktops.JumpExternally(D(1)); // Task View / Win+Ctrl+Arrow
        model.AnchorToCurrent();       // what the watcher triggers

        StatusSnapshot status = model.BuildStatus();
        Assert.True(status.CurrentRow!.IsMain);
        Assert.Equal(D(1).Value, status.CurrentDesktop!.Id);
    }

    [Fact]
    public void A_rows_cursor_is_its_resume_point_not_its_first_desktop()
    {
        var desktops = new FakeDesktopController(new[] { D(0), D(10), D(11) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("work", (10, "code"), (11, "docs")));

        Guid id = model.BuildStatus().Rows.First(r => !r.IsMain).Id!.Value;
        model.GoTo(id, 1, out _);   // land on "docs"
        model.GoTo(null, null, out _); // …then leave for main

        StatusRow row = model.BuildStatus().Rows.First(r => !r.IsMain);
        Assert.Equal(1, row.Cursor); // still pointing at where we left off
        Assert.Equal("docs", row.Desktops[row.Cursor].Label);
    }

    [Fact]
    public void Starting_up_inside_a_branch_publishes_that_branch_not_main()
    {
        // The model's constructor only looks for the current desktop on the main timeline, so a restart
        // while inside a branch — the normal case, since that's where you left off — leaves the cursor
        // claiming main[0]. The app anchors explicitly at startup to correct it; this pins that it must.
        var store = new InMemoryStore();
        var ids = new[] { D(0), D(1), D(10) };
        new NavigationModel(new FakeDesktopController(ids, 0), store).AddBranch(G("work", (10, "code")));

        // Restart with the OS sitting on the branch's desktop.
        var desktops = new FakeDesktopController(ids, 2);
        var model = new NavigationModel(desktops, store);
        model.AnchorToCurrent();

        StatusSnapshot status = model.BuildStatus();
        Assert.False(status.CurrentRow!.IsMain);
        Assert.Equal("work", status.CurrentRow.Name);
        Assert.Equal(D(10).Value, status.CurrentDesktop!.Id);
    }

    [Fact]
    public void Derived_fields_are_not_published()
    {
        // IsMain is computed from Kind. Serialising it would put a second, redundant source of the same
        // fact in the contract — one that is silently ignored on read.
        var model = new NavigationModel(new FakeDesktopController(new[] { D(0) }, 0));
        string json = System.Text.Json.JsonSerializer.Serialize(
            model.BuildStatus(), Hypertree.Cli.StatusJson.Compact);

        Assert.DoesNotContain("isMain", json);
        Assert.Contains("\"kind\"", json);
    }

    [Fact]
    public void Branch_ids_survive_a_restart()
    {
        var store = new InMemoryStore();
        var ids = new[] { D(0), D(10) };

        var first = new NavigationModel(new FakeDesktopController(ids, 0), store);
        first.AddBranch(G("work", (10, "code")));
        Guid before = first.BuildStatus().Rows.First(r => !r.IsMain).Id!.Value;

        var restarted = new NavigationModel(new FakeDesktopController(ids, 0), store);
        Guid after = restarted.BuildStatus().Rows.First(r => !r.IsMain).Id!.Value;

        Assert.Equal(before, after); // an id a caller stored yesterday still resolves today
    }

    [Fact]
    public void State_written_before_ids_existed_is_backfilled_on_load()
    {
        // Upgrade path: a state.json from an older build has no branch id. Loading must mint one rather
        // than leaving an empty GUID that nothing can address.
        var store = new InMemoryStore
        {
            State = new PersistedState
            {
                MainSlot = 0,
                Branches =
                {
                    new PersistedBranch
                    {
                        Name = "legacy",
                        Desktops = { new PersistedDesktop { Id = D(10).Value, Label = "old" } },
                    },
                },
            },
        };

        var model = new NavigationModel(new FakeDesktopController(new[] { D(0), D(10) }, 0), store);

        Guid id = model.BuildStatus().Rows.First(r => !r.IsMain).Id!.Value;
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, store.State.Branches[0].Id); // and it was written back, so it's stable from now on
    }

    [Fact]
    public void Two_branches_may_share_a_name_but_never_an_id()
    {
        // Names are neither unique nor enforced, which is exactly why the contract addresses rows by id.
        var desktops = new FakeDesktopController(new[] { D(0), D(10), D(20) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("dup", (10, "a")));
        model.AddBranch(G("dup", (20, "b")));

        var branches = model.BuildStatus().Rows.Where(r => !r.IsMain).ToList();
        Assert.Equal(2, branches.Count);
        Assert.Equal("dup", branches[0].Name);
        Assert.Equal("dup", branches[1].Name);
        Assert.NotEqual(branches[0].Id, branches[1].Id);
    }
}
