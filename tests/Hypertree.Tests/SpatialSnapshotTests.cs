using System;
using System.Linq;
using Hypertree.Spatial;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the spatial half of a saved layout: capturing room positions / group colours into a
/// <see cref="Snapshot"/>, previewing one as a <see cref="SpatialScene"/>, and re-keying a restored
/// snapshot's spatial facts onto the live <see cref="SpatialState"/>. Pure — no Avalonia, no app.
/// </summary>
public class SpatialSnapshotTests
{
    private static Guid Dg(int n) => new($"{n:D8}-0000-0000-0000-000000000000");
    private static Guid Bg(int n) => new($"{n:D8}-bbbb-0000-0000-000000000000");

    // main (m0, m1) with one branch "feat" (f0) above main (slot 1) and "hotfix" (h0) below.
    private static Snapshot Sample() => new()
    {
        Name = "layout-1",
        MainSlot = 1,
        MainDesktops =
        {
            new PersistedDesktop { Id = Dg(0), Label = "m0" },
            new PersistedDesktop { Id = Dg(1), Label = "m1" },
        },
        Branches =
        {
            new PersistedBranch { Id = Bg(1), Name = "feat", Desktops = { new PersistedDesktop { Id = Dg(10), Label = "f0" } } },
            new PersistedBranch { Id = Bg(2), Name = "hotfix", Desktops = { new PersistedDesktop { Id = Dg(20), Label = "h0" } } },
        },
    };

    // ── Capture ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_records_only_explicit_positions_and_colours()
    {
        Snapshot snap = Sample();
        var state = new SpatialState();
        state.SetPosition(Dg(0), new GridPos(3, 4));   // one placed room
        state.SetColor(Bg(1), "#123456");              // one recoloured group
        // Dg(1)/Dg(10)/Dg(20) unplaced, Bg(2) not recoloured — sparse, so nothing captured for them.

        SpatialSnapshot.Capture(snap, state);

        Assert.Equal(new GridPos(3, 4), Assert.Contains(Dg(0).ToString(), snap.Positions));
        Assert.Single(snap.Positions);
        Assert.Equal("#123456", Assert.Contains(Bg(1).ToString(), snap.GroupColors));
        Assert.Single(snap.GroupColors);
    }

    [Fact]
    public void Capture_replaces_any_stale_tables_on_the_snapshot()
    {
        Snapshot snap = Sample();
        snap.Positions[Dg(99).ToString()] = new GridPos(9, 9); // stale entry from an earlier capture
        snap.GroupColors[Bg(9).ToString()] = "#ffffff";

        SpatialSnapshot.Capture(snap, new SpatialState()); // empty live state
        Assert.Empty(snap.Positions);
        Assert.Empty(snap.GroupColors);
    }

    // ── Preview ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceFrom_emits_groups_in_row_draw_order_with_ids()
    {
        SpatialSource src = SpatialSnapshot.SourceFrom(Sample());

        // slot 1: feat above main, then main (empty-guid bucket), then hotfix below.
        Assert.Equal(new[] { "feat", "main", "hotfix" }, src.Groups.Select(g => g.Name));
        Assert.Equal(Bg(1), src.Groups[0].Id);
        Assert.True(src.Groups[1].IsMain);
        Assert.Equal(Guid.Empty, src.Groups[1].Id);
        Assert.Equal(Bg(2), src.Groups[2].Id);
    }

    [Fact]
    public void SceneFrom_previews_stored_positions_and_colours_over_the_defaults()
    {
        Snapshot snap = Sample();
        snap.Positions[Dg(20).ToString()] = new GridPos(-5, 8); // hotfix/h0 hand-placed
        snap.GroupColors[Bg(1).ToString()] = "#abcdef";         // feat recoloured

        SpatialScene scene = SpatialSnapshot.SceneFrom(snap);

        Assert.Equal(new GridPos(-5, 8), scene.Rooms.Single(r => r.Label == "h0").Pos);
        Assert.Equal("#abcdef", scene.Groups.Single(g => g.Name == "feat").Color);
        // An unplaced room still falls back to the row layout, and main stays neutral.
        Assert.Equal(SpatialPalette.Main, scene.Groups.Single(g => g.IsMain).Color);
    }

    // ── Restore (re-keying) ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyTo_rekeys_positions_and_colours_onto_the_ids_a_restore_produced()
    {
        Snapshot snap = Sample();
        snap.Positions[Dg(0).ToString()] = new GridPos(2, 2);
        snap.GroupColors[Bg(1).ToString()] = "#777777";

        // A restore re-created m0 under a fresh GUID and minted a fresh id for feat.
        Guid newDesktop = Dg(500), newBranch = Bg(500);
        var desktopRemap = new System.Collections.Generic.Dictionary<Guid, Guid> { [Dg(0)] = newDesktop };
        var branchRemap = new System.Collections.Generic.Dictionary<Guid, Guid> { [Bg(1)] = newBranch };

        var target = new SpatialState();
        SpatialSnapshot.ApplyTo(target, snap, desktopRemap, branchRemap);

        Assert.Equal(new GridPos(2, 2), target.Position(newDesktop));
        Assert.Equal("#777777", target.Color(newBranch));
        Assert.Null(target.Position(Dg(0))); // never written under the stale captured id
    }

    [Fact]
    public void ApplyTo_skips_facts_with_no_remap_entry()
    {
        Snapshot snap = Sample();
        snap.Positions[Dg(10).ToString()] = new GridPos(1, 1); // a room dropped by the restore
        snap.GroupColors[Bg(2).ToString()] = "#000000";        // a branch dropped by the restore

        var target = new SpatialState();
        SpatialSnapshot.ApplyTo(target, snap,
            new System.Collections.Generic.Dictionary<Guid, Guid>(),
            new System.Collections.Generic.Dictionary<Guid, Guid>());

        Assert.Empty(target.Positions);
        Assert.Empty(target.GroupColors);
    }

    [Fact]
    public void Capture_then_restore_round_trips_through_a_full_id_remap()
    {
        // Arrange a placed, recoloured live map, capture it, then restore onto brand-new ids.
        Snapshot snap = Sample();
        var live = new SpatialState();
        live.SetPosition(Dg(0), new GridPos(4, 1));
        live.SetPosition(Dg(10), new GridPos(-2, 5));
        live.SetColor(Bg(1), "#0a0b0c");
        SpatialSnapshot.Capture(snap, live);

        var desktopRemap = new System.Collections.Generic.Dictionary<Guid, Guid>
        {
            [Dg(0)] = Dg(900), [Dg(1)] = Dg(901), [Dg(10)] = Dg(910), [Dg(20)] = Dg(920),
        };
        var branchRemap = new System.Collections.Generic.Dictionary<Guid, Guid>
        {
            [Bg(1)] = Bg(900), [Bg(2)] = Bg(901),
        };

        var restored = new SpatialState();
        SpatialSnapshot.ApplyTo(restored, snap, desktopRemap, branchRemap);

        Assert.Equal(new GridPos(4, 1), restored.Position(Dg(900)));
        Assert.Equal(new GridPos(-2, 5), restored.Position(Dg(910)));
        Assert.Equal("#0a0b0c", restored.Color(Bg(900)));
    }
}
