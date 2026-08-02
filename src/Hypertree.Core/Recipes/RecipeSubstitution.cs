using System.Text.RegularExpressions;

namespace Hypertree.Recipes;

/// <summary>
/// Fills a recipe's <c>{name}</c> tokens with supplied values, producing a concrete recipe the executor can
/// run. Pure and OS-free. Substitution is quote-aware in the <em>arguments</em> field: a bare token whose
/// value contains whitespace is wrapped in double quotes so it stays a single argument (a token already
/// written inside quotes is filled in place). Targets and working directories are single values, so they're
/// filled verbatim. An unknown token (no value supplied) is left as-is, so it's visible rather than silently
/// blanked.
/// </summary>
public static class RecipeSubstitution
{
    private static readonly Regex Quoted = new("\"\\{(\\w+)\\}\"", RegexOptions.Compiled);
    private static readonly Regex Bare = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>A copy of <paramref name="recipe"/> with every command's target / arguments / working
    /// directory filled from <paramref name="values"/> (variable names matched case-insensitively).</summary>
    public static Recipe Apply(Recipe recipe, IReadOnlyDictionary<string, string> values)
    {
        var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var result = new Recipe { Name = recipe.Name };
        foreach (RecipeDesktop d in recipe.Desktops)
        {
            var nd = new RecipeDesktop { Label = d.Label };
            foreach (RecipeStep s in d.Steps)
                nd.Steps.Add(new RecipeStep
                {
                    Name = s.Name,
                    Hint = s.Hint,
                    Target = Fill(s.Target, lookup, argContext: false),
                    Arguments = NullIfEmpty(Fill(s.Arguments ?? "", lookup, argContext: true)),
                    WorkingDirectory = NullIfEmpty(Fill(s.WorkingDirectory ?? "", lookup, argContext: false)),
                    Placement = new Placement { Desktop = s.Placement.Desktop, Monitor = s.Placement.Monitor },
                });
            result.Desktops.Add(nd);
        }
        return result;
    }

    private static string Fill(string s, Dictionary<string, string> values, bool argContext)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // A token already written inside quotes: fill in place, keeping the quotes.
        s = Quoted.Replace(s, m => values.TryGetValue(m.Groups[1].Value, out string? v) ? $"\"{v}\"" : m.Value);

        // Bare tokens: fill; in argument context, quote a value that would otherwise split into two args.
        s = Bare.Replace(s, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out string? v)) return m.Value; // unknown → leave visible
            return argContext && v.Any(char.IsWhiteSpace) ? $"\"{v}\"" : v;
        });
        return s;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
