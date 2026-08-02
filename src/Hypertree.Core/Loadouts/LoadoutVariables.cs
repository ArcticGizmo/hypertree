using System.Text.RegularExpressions;

namespace Hypertree.Loadouts;

/// <summary>One variable a loadout needs filled before it can run: its <see cref="Name"/>, a
/// <see cref="Default"/> to prefill (from a declaration, or the built-in), its <see cref="Kind"/>, and
/// whether it's the built-in <c>{dir}</c> (auto-filled from the current directory when applied via the
/// <c>htree</c> CLI, otherwise prompted like any other).</summary>
public sealed record VariableSpec(string Name, string? Default, VariableKind Kind, bool IsDir);

/// <summary>
/// Finds the <c>{name}</c> tokens a loadout uses and turns them into the list of things to prompt for. Pure
/// and OS-free: the App/CLI supply values, <see cref="LoadoutSubstitution"/> applies them. Variables are
/// <em>discovered</em> from the command text so authoring is just typing <c>{repo}</c>; a loadout's declared
/// <see cref="LoadoutVariable"/>s only enrich the prompt (default / kind) for the ones it lists.
/// </summary>
public static class LoadoutVariables
{
    /// <summary>The built-in variable auto-filled from the working directory by the <c>htree</c> CLI.</summary>
    public const string Dir = "dir";

    private static readonly Regex Token = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Distinct variable names used anywhere in the loadout's commands (target / arguments / working
    /// directory), in first-seen order. Case-insensitive — <c>{Repo}</c> and <c>{repo}</c> are one variable,
    /// reported with the casing first seen.</summary>
    public static IReadOnlyList<string> Discover(Loadout loadout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (LoadoutDesktop d in loadout.Desktops)
            foreach (LoadoutStep s in d.Steps)
                foreach (string field in new[] { s.Target, s.Arguments ?? "", s.WorkingDirectory ?? "" })
                    foreach (Match m in Token.Matches(field))
                    {
                        string name = m.Groups[1].Value;
                        if (seen.Add(name)) order.Add(name);
                    }
        return order;
    }

    /// <summary>The loadout's variables as prompt specs: each discovered name, enriched with a declared
    /// default / kind when the loadout declares one, and flagged (and defaulted to <see cref="VariableKind.Folder"/>)
    /// when it's the built-in <c>{dir}</c>.</summary>
    public static IReadOnlyList<VariableSpec> Prompts(Loadout loadout)
    {
        var specs = new List<VariableSpec>();
        foreach (string name in Discover(loadout))
        {
            LoadoutVariable? declared = loadout.Variables
                .FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool isDir = name.Equals(Dir, StringComparison.OrdinalIgnoreCase);
            VariableKind kind = declared?.Kind ?? (isDir ? VariableKind.Folder : VariableKind.Text);
            specs.Add(new VariableSpec(name, declared?.Default, kind, isDir));
        }
        return specs;
    }
}
