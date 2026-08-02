using Hypertree.Launch;
using Hypertree.Recipes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The OS-free recipe generator: <see cref="RecipeBuilder.FromCapture"/> turning a branch's per-desktop app
/// captures into a named recipe — one desktop per captured desktop, one step per app, each placed on its
/// own desktop's label. Empty desktops drop out (a recipe records work, not empty rooms).
/// </summary>
public class RecipeBuilderTests
{
    private static (string, IReadOnlyList<CapturedApp>) Desk(string label, params string[] paths)
        => (label, paths.Select(p => new CapturedApp(p, System.IO.Path.GetFileNameWithoutExtension(p))).ToList());

    [Fact]
    public void Builds_a_desktop_per_capture_and_a_step_per_app()
    {
        Recipe r = RecipeBuilder.FromCapture("feat-1", new[]
        {
            Desk("api", @"C:\Code.exe", @"C:\wt.exe"),
            Desk("web", @"C:\firefox.exe"),
        });

        Assert.Equal("feat-1", r.Name);
        Assert.Equal(new[] { "api", "web" }, r.Desktops.Select(d => d.Label));
        Assert.Equal(new[] { @"C:\Code.exe", @"C:\wt.exe" }, r.Desktops[0].Steps.Select(s => s.Target));
        Assert.Equal(3, r.StepCount);
    }

    [Fact]
    public void Each_step_is_placed_on_its_own_desktop_label()
    {
        Recipe r = RecipeBuilder.FromCapture("feat", new[] { Desk("api", @"C:\Code.exe") });
        Assert.Equal("api", r.Desktops[0].Steps[0].Placement.Desktop);
    }

    [Fact]
    public void Carries_monitor_and_hint_into_the_step()
    {
        var apps = (IReadOnlyList<CapturedApp>)new[] { new CapturedApp(@"C:\Code.exe", "Code", Monitor: 2, Hint: "myrepo — Code") };
        Recipe r = RecipeBuilder.FromCapture("feat", new[] { ("api", apps) });

        RecipeStep step = r.Desktops[0].Steps[0];
        Assert.Equal(2, step.Placement.Monitor);
        Assert.Equal("myrepo — Code", step.Hint);
    }

    [Fact]
    public void An_unknown_monitor_leaves_placement_monitor_null()
    {
        Recipe r = RecipeBuilder.FromCapture("feat", new[] { Desk("api", @"C:\Code.exe") }); // Monitor 0
        Assert.Null(r.Desktops[0].Steps[0].Placement.Monitor);
    }

    [Fact]
    public void Drops_desktops_with_no_apps()
    {
        Recipe r = RecipeBuilder.FromCapture("feat", new[]
        {
            Desk("empty"),                       // no apps → dropped
            Desk("api", @"C:\Code.exe"),
        });

        Assert.Equal(new[] { "api" }, r.Desktops.Select(d => d.Label));
    }

    [Fact]
    public void A_branch_with_nothing_open_yields_an_empty_recipe()
    {
        Recipe r = RecipeBuilder.FromCapture("feat", new[] { Desk("api"), Desk("web") });
        Assert.Empty(r.Desktops);
        Assert.Equal(0, r.StepCount);
    }
}
