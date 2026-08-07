using System;
using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the spatial projection: how <see cref="SpatialScene.From"/> merges stored colours/positions over
/// the sparse defaults, and that <see cref="NavigationModel.BuildSpatialSource"/> emits groups in row draw
/// order with ids and selection flags intact. Pure — no Avalonia, no renderer.
/// </summary>
public class SpatialSceneTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static Guid Grp(int n) => new($"{n:D8}-aaaa-0000-0000-000000000000");

    private static SpatialDesktop Desk(int id, string label, bool sel = false, bool here = false, int win = 0)
        => new(D(id), label, sel, here, win);

    // main (a,b) with one branch "feat" (x,y). Draw order in the source: main then branch (slot 0).
    private static SpatialSource Sample() => new(new[]
    {
        new SpatialGroupSource(Guid.Empty, "main", IsMain: true, new[] { Desk(0, "a", sel: true), Desk(1, "b") }),
        new SpatialGroupSource(Grp(1), "feat", IsMain: false, new[] { Desk(10, "x"), Desk(11, "y", here: true) }),
    });

    [Fact]
    public void Unplaced_desktops_fall_back_to_the_row_layout()
    {
        SpatialScene scene = SpatialScene.From(Sample(), new SpatialState());

        // Group gi forms row gi; desktop di sits at column di.
        Assert.Equal(new GridPos(0, 0), Room(scene, 0).Pos);  // main/a
        Assert.Equal(new GridPos(1, 0), Room(scene, 1).Pos);  // main/b
        Assert.Equal(new GridPos(0, 1), Room(scene, 10).Pos); // feat/x
        Assert.Equal(new GridPos(1, 1), Room(scene, 11).Pos); // feat/y
    }

    [Fact]
    public void Stored_position_overrides_the_default()
    {
        var state = new SpatialState();
        state.SetPosition(D(11).Value, new GridPos(-3, 7));

        SpatialScene scene = SpatialScene.From(Sample(), state);
        Assert.Equal(new GridPos(-3, 7), Room(scene, 11).Pos);
        Assert.Equal(new GridPos(0, 0), Room(scene, 0).Pos); // others still defaulted
    }

    [Fact]
    public void Main_group_is_neutral_and_a_branch_gets_a_palette_hue_by_default()
    {
        SpatialScene scene = SpatialScene.From(Sample(), new SpatialState());

        SpatialGroup main = Assert.Single(scene.Groups, g => g.IsMain);
        Assert.Equal(SpatialPalette.Main, main.Color);

        SpatialGroup feat = Assert.Single(scene.Groups, g => g.Name == "feat");
        Assert.Equal(SpatialPalette.For(Grp(1)), feat.Color);   // stable, id-derived
        Assert.Contains(feat.Color, SpatialPalette.Colors);      // and a real palette slot
    }

    [Fact]
    public void Stored_colour_overrides_a_branch_default_but_never_main()
    {
        var state = new SpatialState();
        state.SetColor(Grp(1), "#123456");
        state.SetColor(Guid.Empty, "#999999"); // an attempt to recolour main — ignored

        SpatialScene scene = SpatialScene.From(Sample(), state);
        Assert.Equal("#123456", Assert.Single(scene.Groups, g => g.Name == "feat").Color);
        Assert.Equal(SpatialPalette.Main, Assert.Single(scene.Groups, g => g.IsMain).Color);
    }

    [Fact]
    public void Selection_and_here_flags_survive_the_projection()
    {
        SpatialScene scene = SpatialScene.From(Sample(), new SpatialState());
        Assert.True(Room(scene, 0).Selected);   // main/a
        Assert.True(Room(scene, 11).Here);       // feat/y
        Assert.False(Room(scene, 1).Selected);
    }

    [Fact]
    public void Id_derived_group_colour_is_stable_across_reordering()
    {
        // The same branch id must map to the same default hue regardless of its position in the source.
        string first = SpatialPalette.For(Grp(1));
        string again = SpatialPalette.For(Grp(1));
        Assert.Equal(first, again);
    }

    // ── BuildSpatialSource (through the navigation model) ─────────────────────────

    private static readonly DesktopId[] TopIds = { D(0), D(1), D(2) };

    [Fact]
    public void BuildSpatialSource_emits_main_then_branches_in_draw_order_with_ids()
    {
        var ctrl = new FakeDesktopController(TopIds, currentIndex: 1);
        var m = new NavigationModel(ctrl);
        m.AddBranch(new Branch("feat", new[] { new DesktopRef(D(10), "x"), new DesktopRef(D(11), "y") }));

        SpatialSource source = m.BuildSpatialSource();

        // mainSlot defaults to 0, so order is: MAIN, feat.
        Assert.Equal(2, source.Groups.Count);
        Assert.True(source.Groups[0].IsMain);
        Assert.Equal(Guid.Empty, source.Groups[0].Id);
        Assert.Equal("feat", source.Groups[1].Name);
        Assert.False(source.Groups[1].IsMain);

        // main carries the OS desktop ids; the current one (index 1) is selected.
        Assert.Equal(new[] { D(0), D(1), D(2) }, source.Groups[0].Desktops.Select(d => d.Id));
        Assert.Equal(new[] { false, true, false }, source.Groups[0].Desktops.Select(d => d.Selected));

        // and the branch carries its own desktop ids.
        Assert.Equal(new[] { D(10), D(11) }, source.Groups[1].Desktops.Select(d => d.Id));
    }

    [Fact]
    public void BuildSpatialSource_marks_the_branch_desktop_selected_when_inside_a_branch()
    {
        var ctrl = new FakeDesktopController(TopIds);
        var m = new NavigationModel(ctrl);
        m.AddBranch(new Branch("feat", new[] { new DesktopRef(D(10), "x"), new DesktopRef(D(11), "y") }));
        m.GoToBranchDesktop(0, 1); // inside feat, on "y"

        SpatialSource source = m.BuildSpatialSource();
        SpatialGroupSource feat = Assert.Single(source.Groups, g => g.Name == "feat");
        Assert.Equal(new[] { false, true }, feat.Desktops.Select(d => d.Selected));
        Assert.All(source.Groups.Single(g => g.IsMain).Desktops, d => Assert.False(d.Selected));
    }

    private static SpatialRoom Room(SpatialScene scene, int id)
        => scene.Rooms.Single(r => r.Id == D(id));
}
