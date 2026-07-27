using Hypertree.Desktops;
using Hypertree.Scopes;
using Hypertree.Status;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the jump the control pipe exposes: addressing a row by stable id, the resume-point semantics of
/// a bare "go to this branch", and the two ways a target can fail to resolve.
/// </summary>
public class GoToTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static Branch G(string name, params (int id, string label)[] desks)
        => new(name, desks.Select(d => new DesktopRef(D(d.id), d.label)).ToList());

    private static (NavigationModel model, FakeDesktopController desktops) Setup()
    {
        var desktops = new FakeDesktopController(new[] { D(0), D(1), D(10), D(11) }, 0);
        var model = new NavigationModel(desktops);
        model.AddBranch(G("work", (10, "code"), (11, "docs")));
        return (model, desktops);
    }

    private static Guid BranchId(NavigationModel model)
        => model.BuildStatus().Rows.First(r => !r.IsMain).Id!.Value;

    [Fact]
    public void Going_to_a_branch_lands_on_its_resume_desktop_not_its_first()
    {
        var (model, desktops) = Setup();
        Guid id = BranchId(model);

        model.GoTo(id, 1, out _);      // be on "docs"
        model.GoTo(null, null, out _); // leave for main
        desktops.Switches.Clear();

        Assert.Equal(GoToResult.Ok, model.GoTo(id, null, out string landed));
        Assert.Equal(D(11), desktops.Switches.Single()); // "docs", not "code"
        Assert.Equal("work/docs", landed);
    }

    [Fact]
    public void Going_to_main_lands_on_its_remembered_desktop()
    {
        var (model, desktops) = Setup();
        model.GoToTop(1);              // remember main at D(1)
        model.GoTo(BranchId(model), null, out _);
        desktops.Switches.Clear();

        Assert.Equal(GoToResult.Ok, model.GoTo(null, null, out string landed));
        Assert.Equal(D(1), desktops.Switches.Single()); // not index 0
        Assert.Equal("main/d1", landed);
    }

    [Fact]
    public void A_specific_desktop_can_be_named()
    {
        var (model, desktops) = Setup();
        Assert.Equal(GoToResult.Ok, model.GoTo(BranchId(model), 0, out string landed));
        Assert.Equal(D(10), desktops.Switches.Last());
        Assert.Equal("work/code", landed);
    }

    [Fact]
    public void An_unknown_branch_id_is_reported_rather_than_guessed_at()
    {
        var (model, desktops) = Setup();
        desktops.Switches.Clear();

        Assert.Equal(GoToResult.NoSuchBranch, model.GoTo(Guid.NewGuid(), null, out _));
        Assert.Empty(desktops.Switches); // nothing moved
    }

    [Fact]
    public void An_out_of_range_desktop_is_reported_rather_than_clamped()
    {
        // Clamping would silently land the caller somewhere they didn't ask for; a CLI needs to say no.
        var (model, desktops) = Setup();
        desktops.Switches.Clear();

        Assert.Equal(GoToResult.NoSuchDesktop, model.GoTo(BranchId(model), 9, out _));
        Assert.Empty(desktops.Switches);
    }

    [Fact]
    public void An_id_still_resolves_after_the_stack_is_reordered()
    {
        // The whole point of ids: a caller that read the layout, then jumped, must not be misdirected by
        // a reorder in between — which is exactly what addressing by list position would allow.
        var (model, desktops) = Setup();
        model.AddBranch(G("other", (1, "x")));

        Guid work = model.BuildStatus().Rows.First(r => r.Name == "work").Id!.Value;
        model.MoveBranchToRow(model.IndexOfBranch(work), 0); // shove it to the top of the stack
        desktops.Switches.Clear();

        Assert.Equal(GoToResult.Ok, model.GoTo(work, 0, out string landed));
        Assert.Equal("work/code", landed);
        Assert.Equal(D(10), desktops.Switches.Single());
    }

    [Fact]
    public void Going_where_you_already_are_succeeds_without_switching()
    {
        var (model, desktops) = Setup();
        Guid id = BranchId(model);
        model.GoTo(id, 0, out _);
        desktops.Switches.Clear();

        // Commit() early-returns when the target desktop is unchanged. That's a no-op, not a failure —
        // a caller asking to be somewhere it already is has got what it wanted.
        Assert.Equal(GoToResult.Ok, model.GoTo(id, 0, out _));
        Assert.Empty(desktops.Switches);
    }
}
