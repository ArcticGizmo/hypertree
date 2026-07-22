using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the entire *feel* of Model P (PLAN.md §3) against a fake controller: move-within-level,
/// dive/surface, resume-last-used, and every edge/no-op from §5 — before a single hotkey or Win32
/// call exists. If these pass, the model is correct independent of whether the OS interop works.
/// </summary>
public class NavigationModelTests
{
    // Fixture ids. Anchors: Web / API / Mobile. Scopes: feat-123 under Web (SPA/API/Mobile),
    // feat-456 under Mobile (X/Y). API has no scope (tests the scope-less dive no-op).
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
    private static readonly DesktopId Web = D(0), Api = D(1), Mob = D(2);
    private static readonly DesktopId Spa = D(10), ScApi = D(11), ScMob = D(12);
    private static readonly DesktopId X = D(20), Y = D(21);

    private static readonly DesktopId[] AllDesktops = { Web, Api, Mob, Spa, ScApi, ScMob, X, Y };

    private static Topology BuildTopology() => new(new[]
    {
        new Anchor(new DesktopRef(Web, "Web"), new Scope("feat-123", new[]
        {
            new DesktopRef(Spa, "SPA"), new DesktopRef(ScApi, "API"), new DesktopRef(ScMob, "Mobile"),
        })),
        new Anchor(new DesktopRef(Api, "API")),                       // no scope
        new Anchor(new DesktopRef(Mob, "Mobile"), new Scope("feat-456", new[]
        {
            new DesktopRef(X, "X"), new DesktopRef(Y, "Y"),
        })),
    });

    private static (NavigationModel model, FakeDesktopController ctrl) New(int currentIndex = 0)
    {
        var ctrl = new FakeDesktopController(AllDesktops, currentIndex);
        return (new NavigationModel(BuildTopology(), ctrl), ctrl);
    }

    // ── Initialization ─────────────────────────────────────────────────────────

    [Fact]
    public void Starts_on_the_anchor_the_os_is_showing()
    {
        var (model, ctrl) = New(currentIndex: 1); // OS is on the API desktop
        Assert.Equal("API (2/3)", model.Location.Format());
        Assert.Empty(ctrl.Switches); // construction never switches
    }

    [Fact]
    public void Falls_back_to_first_anchor_when_current_is_unknown()
    {
        var ctrl = new FakeDesktopController(AllDesktops, currentIndex: 3); // OS on a scope desktop, not an anchor
        var model = new NavigationModel(BuildTopology(), ctrl);
        Assert.Equal("Web (1/3)", model.Location.Format());
    }

    // ── Move within the day-to-day row ──────────────────────────────────────────

    [Fact]
    public void MoveRight_walks_the_anchor_row()
    {
        var (model, ctrl) = New();
        Assert.True(model.Apply(NavAction.MoveRight));
        Assert.Equal(Api, ctrl.Current);
        Assert.Equal("API (2/3)", model.Location.Format());
    }

    [Fact]
    public void MoveLeft_at_left_edge_is_a_noop()
    {
        var (model, ctrl) = New(currentIndex: 0);
        Assert.False(model.Apply(NavAction.MoveLeft));
        Assert.Empty(ctrl.Switches);
        Assert.Equal("Web (1/3)", model.Location.Format());
    }

    [Fact]
    public void MoveRight_at_right_edge_is_a_noop()
    {
        var (model, ctrl) = New(currentIndex: 2); // on Mobile, the last anchor
        Assert.False(model.Apply(NavAction.MoveRight));
        Assert.Empty(ctrl.Switches);
    }

    // ── Dive / surface ──────────────────────────────────────────────────────────

    [Fact]
    public void Dive_enters_the_current_anchors_scope_at_its_first_desktop()
    {
        var (model, ctrl) = New();
        Assert.True(model.Apply(NavAction.Dive));
        Assert.Equal(Spa, ctrl.Current);
        Assert.Equal("▸ feat-123 · SPA (1/3)", model.Location.Format());
    }

    [Fact]
    public void Dive_on_a_scopeless_anchor_is_a_noop()
    {
        var (model, ctrl) = New(currentIndex: 1); // API anchor has no scope
        Assert.False(model.Apply(NavAction.Dive));
        Assert.Empty(ctrl.Switches);
    }

    [Fact]
    public void Dive_uses_the_current_anchors_scope_not_another()
    {
        var (model, ctrl) = New();
        model.Apply(NavAction.MoveRight); // Web -> API
        model.Apply(NavAction.MoveRight); // API -> Mobile
        Assert.True(model.Apply(NavAction.Dive));
        Assert.Equal(X, ctrl.Current); // feat-456's first desktop, not feat-123's
        Assert.Equal("▸ feat-456 · X (1/2)", model.Location.Format());
    }

    [Fact]
    public void Surface_at_day_to_day_is_a_noop()
    {
        var (model, ctrl) = New();
        Assert.False(model.Apply(NavAction.Surface));
        Assert.Empty(ctrl.Switches);
    }

    [Fact]
    public void Surface_returns_to_the_entry_anchor_from_anywhere_in_the_scope()
    {
        var (model, ctrl) = New();
        model.Apply(NavAction.Dive);       // -> SPA
        model.Apply(NavAction.MoveRight);  // -> scope API
        model.Apply(NavAction.MoveRight);  // -> scope Mobile (deep in the scope)
        Assert.True(model.Apply(NavAction.Surface));
        Assert.Equal(Web, ctrl.Current);   // back on the anchor, not some scope desktop
        Assert.Equal("Web (1/3)", model.Location.Format());
    }

    // ── Move within a scope ───────────────────────────────────────────────────────

    [Fact]
    public void Move_within_scope_walks_the_scope_strip_and_clamps()
    {
        var (model, ctrl) = New();
        model.Apply(NavAction.Dive);                       // SPA (1/3)
        Assert.True(model.Apply(NavAction.MoveRight));     // API (2/3)
        Assert.Equal("▸ feat-123 · API (2/3)", model.Location.Format());
        Assert.True(model.Apply(NavAction.MoveRight));     // Mobile (3/3)
        Assert.False(model.Apply(NavAction.MoveRight));    // clamp at the scope's right edge
        Assert.Equal(ScMob, ctrl.Current);
    }

    // ── Resume last-used (the decision that makes a scope feel persistent) ──────────

    [Fact]
    public void Rediving_resumes_the_last_used_desktop_not_the_first()
    {
        var (model, ctrl) = New();
        model.Apply(NavAction.Dive);       // SPA
        model.Apply(NavAction.MoveRight);  // scope API (this is now "last used")
        model.Apply(NavAction.Surface);    // back to Web anchor
        ctrl.Switches.Clear();

        Assert.True(model.Apply(NavAction.Dive)); // re-dive
        Assert.Equal(ScApi, ctrl.Current);        // resumed at API, not SPA
        Assert.Equal("▸ feat-123 · API (2/3)", model.Location.Format());
        Assert.Single(ctrl.Switches);
    }

    [Fact]
    public void Each_scope_resumes_independently()
    {
        var (model, _) = New();
        // Leave feat-123 deep (on Mobile)
        model.Apply(NavAction.Dive);
        model.Apply(NavAction.MoveRight);
        model.Apply(NavAction.MoveRight); // feat-123 Mobile
        model.Apply(NavAction.Surface);

        // Go to feat-456, leave it on Y
        model.Apply(NavAction.MoveRight); // API
        model.Apply(NavAction.MoveRight); // Mobile anchor
        model.Apply(NavAction.Dive);      // feat-456 X
        model.Apply(NavAction.MoveRight); // feat-456 Y
        model.Apply(NavAction.Surface);

        // Re-dive each: feat-456 resumes Y, feat-123 resumes Mobile
        model.Apply(NavAction.Dive);
        Assert.Equal("▸ feat-456 · Y (2/2)", model.Location.Format());
        model.Apply(NavAction.Surface);
        model.Apply(NavAction.MoveLeft);  // API
        model.Apply(NavAction.MoveLeft);  // Web
        model.Apply(NavAction.Dive);
        Assert.Equal("▸ feat-123 · Mobile (3/3)", model.Location.Format());
    }

    // ── Change signalling ─────────────────────────────────────────────────────────

    [Fact]
    public void Changed_fires_only_on_real_movement()
    {
        var (model, _) = New();
        int changes = 0;
        model.Changed += () => changes++;

        model.Apply(NavAction.MoveLeft);  // no-op at edge
        model.Apply(NavAction.Surface);   // no-op at day-to-day
        Assert.Equal(0, changes);

        model.Apply(NavAction.MoveRight); // real move
        model.Apply(NavAction.MoveLeft);  // real move
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Format_renders_top_row_without_the_scope_marker()
    {
        var (model, _) = New();
        Assert.Equal("Web (1/3)", model.Location.Format());
        Assert.False(model.Location.InScope);
    }
}
