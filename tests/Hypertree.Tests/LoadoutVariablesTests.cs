using Hypertree.Loadouts;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Discovering a loadout's <c>{name}</c> tokens and filling them (<see cref="LoadoutVariables"/> /
/// <see cref="LoadoutSubstitution"/>) — the OS-free half of parameterised loadouts: author with tokens, fill
/// once at apply time, outfit any project. The App/CLI supply the values; this decides what's asked for and
/// how it's substituted.
/// </summary>
public class LoadoutVariablesTests
{
    private static Loadout Loadout(params LoadoutStep[] steps)
    {
        var r = new Loadout { Name = "dev" };
        r.Desktops.Add(new LoadoutDesktop { Label = "code", Steps = steps.ToList() });
        return r;
    }

    private static LoadoutStep Step(string target, string? args = null, string? dir = null)
        => new() { Target = target, Arguments = args, WorkingDirectory = dir, Placement = new Placement { Desktop = "code" } };

    // ── Discovery ────────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_finds_distinct_tokens_across_fields_in_first_seen_order()
    {
        Loadout r = Loadout(
            Step("code", "{repo}", "{repo}"),
            Step("wt", "-d {repo}", null),
            Step("pwsh", "-Command \"cd {repo}; npm run dev -- --port {port}\""));

        Assert.Equal(new[] { "repo", "port" }, LoadoutVariables.Discover(r));
    }

    [Fact]
    public void Discover_treats_a_token_case_insensitively_keeping_first_casing()
    {
        Loadout r = Loadout(Step("code", "{Repo}"), Step("wt", "-d {repo}"));
        Assert.Equal(new[] { "Repo" }, LoadoutVariables.Discover(r));
    }

    [Fact]
    public void Prompts_attach_declared_default_and_kind_and_flag_the_dir_builtin()
    {
        Loadout r = Loadout(Step("code", "{repo}", "{dir}"));
        r.Variables.Add(new LoadoutVariable { Name = "repo", Default = @"C:\repos\app", Kind = VariableKind.Folder });

        var prompts = LoadoutVariables.Prompts(r);
        VariableSpec repo = prompts.Single(p => p.Name == "repo");
        Assert.Equal(@"C:\repos\app", repo.Default);
        Assert.Equal(VariableKind.Folder, repo.Kind);
        Assert.False(repo.IsDir);

        VariableSpec dir = prompts.Single(p => p.Name == "dir");
        Assert.True(dir.IsDir);
        Assert.Equal(VariableKind.Folder, dir.Kind); // {dir} defaults to a folder even without a declaration
        Assert.Null(dir.Default);
    }

    // ── Substitution ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_fills_target_arguments_and_working_directory()
    {
        Loadout r = Loadout(Step("code", "{repo}", "{repo}"));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = @"C:\repos\app" });

        LoadoutStep s = filled.Desktops[0].Steps[0];
        Assert.Equal("code", s.Target);
        Assert.Equal(@"C:\repos\app", s.Arguments);
        Assert.Equal(@"C:\repos\app", s.WorkingDirectory);
    }

    [Fact]
    public void Apply_quotes_a_spacey_value_that_would_split_an_argument()
    {
        Loadout r = Loadout(Step("wt", "-d {dir}"));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        Assert.Equal("-d \"C:\\my proj\"", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_does_not_double_quote_a_token_already_in_quotes()
    {
        Loadout r = Loadout(Step("wt", "-d \"{dir}\""));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        Assert.Equal("-d \"C:\\my proj\"", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_leaves_the_target_and_working_dir_unquoted_even_with_spaces()
    {
        // These are single-value fields, not command lines — the shell takes them whole.
        Loadout r = Loadout(Step("{dir}", null, "{dir}"));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        LoadoutStep s = filled.Desktops[0].Steps[0];
        Assert.Equal(@"C:\my proj", s.Target);
        Assert.Equal(@"C:\my proj", s.WorkingDirectory);
    }

    [Fact]
    public void Apply_matches_variable_names_case_insensitively()
    {
        Loadout r = Loadout(Step("code", "{Repo}"));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = @"C:\x" });
        Assert.Equal(@"C:\x", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_leaves_an_unknown_token_visible()
    {
        Loadout r = Loadout(Step("code", "{repo} {missing}"));
        Loadout filled = LoadoutSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = "X" });
        Assert.Equal("X {missing}", filled.Desktops[0].Steps[0].Arguments);
    }
}
