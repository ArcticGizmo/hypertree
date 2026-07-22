using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the render-ready map snapshot and runtime scope define/remove — the data the HUD overlay
/// draws, and the operations behind the tray's "New/Remove scope here".
/// </summary>
public class NavMapTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId Web = D(0), Api = D(1), Mob = D(2), Spa = D(10), ScApi = D(11), ScMob = D(12);
    private static readonly DesktopId[] All = { Web, Api, Mob, Spa, ScApi, ScMob };

    private static (NavigationModel model, FakeDesktopController ctrl) New(int current = 0)
    {
        var topo = new Topology(new[]
        {
            new Anchor(new DesktopRef(Web, "Web"), new Scope("feat-123", new[]
            {
                new DesktopRef(Spa, "SPA"), new DesktopRef(ScApi, "API"), new DesktopRef(ScMob, "Mobile"),
            })),
            new Anchor(new DesktopRef(Api, "API")),
        });
        var ctrl = new FakeDesktopController(All, current);
        return (new NavigationModel(topo, ctrl), ctrl);
    }

    [Fact]
    public void Map_on_top_row_marks_current_anchor_and_shows_scope_dimmed()
    {
        var (model, _) = New();
        NavMap m = model.BuildMap();

        Assert.False(m.InScope);
        Assert.Equal(2, m.Anchors.Count);
        Assert.True(m.Anchors[0].IsCurrentColumn);
        Assert.True(m.Anchors[0].HasScope);
        Assert.False(m.Anchors[1].IsCurrentColumn);

        // Scope is shown (a dive target) but nothing in it is "current" while on the top row.
        Assert.NotNull(m.ScopeDesktops);
        Assert.Equal("feat-123", m.ScopeName);
        Assert.All(m.ScopeDesktops!, d => Assert.False(d.IsCurrent));
    }

    [Fact]
    public void Map_when_dived_marks_the_current_scope_desktop()
    {
        var (model, _) = New();
        model.Apply(NavAction.Dive);
        model.Apply(NavAction.MoveRight); // scope API
        NavMap m = model.BuildMap();

        Assert.True(m.InScope);
        Assert.Equal(new[] { false, true, false }, m.ScopeDesktops!.Select(d => d.IsCurrent));
        Assert.True(m.Anchors[0].IsCurrentColumn); // still the owning column
    }

    [Fact]
    public void Map_has_no_scope_row_for_a_scopeless_anchor()
    {
        var (model, _) = New();
        model.Apply(NavAction.MoveRight); // to API anchor (no scope)
        NavMap m = model.BuildMap();
        Assert.Null(m.ScopeDesktops);
        Assert.Null(m.ScopeName);
    }

    [Fact]
    public void DefineScopeHere_attaches_a_scope_that_can_be_dived()
    {
        var (model, ctrl) = New();
        model.Apply(NavAction.MoveRight); // API anchor, scope-less
        Assert.False(model.CurrentAnchorHasScope);

        var previous = model.DefineScopeHere(new Scope("hotfix", new[]
        {
            new DesktopRef(D(30), "one"), new DesktopRef(D(31), "two"),
        }));
        Assert.Null(previous);
        Assert.True(model.CurrentAnchorHasScope);

        Assert.True(model.Apply(NavAction.Dive));
        Assert.Equal(D(30), ctrl.Current);
        Assert.Equal("▸ hotfix · one (1/2)", model.Location.Format());
    }

    [Fact]
    public void DefineScopeHere_returns_the_replaced_scope_for_teardown()
    {
        var (model, _) = New(); // Web anchor already has feat-123
        Scope? previous = model.DefineScopeHere(new Scope("new", new[] { new DesktopRef(D(40), "x") }));
        Assert.NotNull(previous);
        Assert.Equal("feat-123", previous!.Name);
    }

    [Fact]
    public void RemoveScopeHere_detaches_and_returns_the_scope()
    {
        var (model, _) = New();
        Scope? removed = model.RemoveScopeHere();
        Assert.Equal("feat-123", removed!.Name);
        Assert.False(model.CurrentAnchorHasScope);
        Assert.False(model.Apply(NavAction.Dive)); // now a no-op — nothing to dive into
    }

    [Fact]
    public void Defining_a_scope_while_dived_is_rejected()
    {
        var (model, _) = New();
        model.Apply(NavAction.Dive);
        Assert.Throws<InvalidOperationException>(() =>
            model.DefineScopeHere(new Scope("x", new[] { new DesktopRef(D(50), "y") })));
    }
}
