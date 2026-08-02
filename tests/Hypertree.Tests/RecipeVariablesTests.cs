using Hypertree.Recipes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Discovering a recipe's <c>{name}</c> tokens and filling them (<see cref="RecipeVariables"/> /
/// <see cref="RecipeSubstitution"/>) — the OS-free half of parameterised recipes: author with tokens, fill
/// once at apply time, outfit any project. The App/CLI supply the values; this decides what's asked for and
/// how it's substituted.
/// </summary>
public class RecipeVariablesTests
{
    private static Recipe Recipe(params RecipeStep[] steps)
    {
        var r = new Recipe { Name = "dev" };
        r.Desktops.Add(new RecipeDesktop { Label = "code", Steps = steps.ToList() });
        return r;
    }

    private static RecipeStep Step(string target, string? args = null, string? dir = null)
        => new() { Target = target, Arguments = args, WorkingDirectory = dir, Placement = new Placement { Desktop = "code" } };

    // ── Discovery ────────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_finds_distinct_tokens_across_fields_in_first_seen_order()
    {
        Recipe r = Recipe(
            Step("code", "{repo}", "{repo}"),
            Step("wt", "-d {repo}", null),
            Step("pwsh", "-Command \"cd {repo}; npm run dev -- --port {port}\""));

        Assert.Equal(new[] { "repo", "port" }, RecipeVariables.Discover(r));
    }

    [Fact]
    public void Discover_treats_a_token_case_insensitively_keeping_first_casing()
    {
        Recipe r = Recipe(Step("code", "{Repo}"), Step("wt", "-d {repo}"));
        Assert.Equal(new[] { "Repo" }, RecipeVariables.Discover(r));
    }

    [Fact]
    public void Prompts_attach_declared_default_and_kind_and_flag_the_dir_builtin()
    {
        Recipe r = Recipe(Step("code", "{repo}", "{dir}"));
        r.Variables.Add(new RecipeVariable { Name = "repo", Default = @"C:\repos\app", Kind = VariableKind.Folder });

        var prompts = RecipeVariables.Prompts(r);
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
        Recipe r = Recipe(Step("code", "{repo}", "{repo}"));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = @"C:\repos\app" });

        RecipeStep s = filled.Desktops[0].Steps[0];
        Assert.Equal("code", s.Target);
        Assert.Equal(@"C:\repos\app", s.Arguments);
        Assert.Equal(@"C:\repos\app", s.WorkingDirectory);
    }

    [Fact]
    public void Apply_quotes_a_spacey_value_that_would_split_an_argument()
    {
        Recipe r = Recipe(Step("wt", "-d {dir}"));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        Assert.Equal("-d \"C:\\my proj\"", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_does_not_double_quote_a_token_already_in_quotes()
    {
        Recipe r = Recipe(Step("wt", "-d \"{dir}\""));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        Assert.Equal("-d \"C:\\my proj\"", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_leaves_the_target_and_working_dir_unquoted_even_with_spaces()
    {
        // These are single-value fields, not command lines — the shell takes them whole.
        Recipe r = Recipe(Step("{dir}", null, "{dir}"));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["dir"] = @"C:\my proj" });
        RecipeStep s = filled.Desktops[0].Steps[0];
        Assert.Equal(@"C:\my proj", s.Target);
        Assert.Equal(@"C:\my proj", s.WorkingDirectory);
    }

    [Fact]
    public void Apply_matches_variable_names_case_insensitively()
    {
        Recipe r = Recipe(Step("code", "{Repo}"));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = @"C:\x" });
        Assert.Equal(@"C:\x", filled.Desktops[0].Steps[0].Arguments);
    }

    [Fact]
    public void Apply_leaves_an_unknown_token_visible()
    {
        Recipe r = Recipe(Step("code", "{repo} {missing}"));
        Recipe filled = RecipeSubstitution.Apply(r, new Dictionary<string, string> { ["repo"] = "X" });
        Assert.Equal("X {missing}", filled.Desktops[0].Steps[0].Arguments);
    }
}
