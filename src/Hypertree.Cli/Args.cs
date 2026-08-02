namespace Hypertree.Cli;

/// <summary>
/// A minimal command-line parse: positional words, boolean flags, and <c>--name value</c> pairs.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a parsing library because the entire surface is four commands and a
/// handful of flags, and a dependency here would cost more than it saves — including at startup, which is
/// the one thing this tool is optimised for. <c>--name=value</c> and <c>--name value</c> are both accepted
/// because both are muscle memory depending on which tools you grew up with.
/// </remarks>
internal sealed class Args
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Non-flag words, in order. <c>[0]</c> is the command.</summary>
    public List<string> Positional { get; } = new();

    /// <summary>Flags that take a value when present. Anything else is treated as boolean.</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal) { "--id" };

    public static Args Parse(string[] argv)
    {
        var args = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (!a.StartsWith('-')) { args.Positional.Add(a); continue; }

            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                args._values[a[..eq]] = a[(eq + 1)..];
                continue;
            }

            if (ValueFlags.Contains(a) && i + 1 < argv.Length && !argv[i + 1].StartsWith('-'))
            {
                args._values[a] = argv[++i];
                continue;
            }

            args._flags.Add(a);
        }
        return args;
    }

    public string? Command => Positional.Count > 0 ? Positional[0] : null;

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string flag) => _values.TryGetValue(flag, out string? v) ? v : null;

    /// <summary>Every <c>--name=value</c> pair given, keyed by the flag (with its leading dashes). Used by
    /// <c>populate</c>, where any such pair is a loadout variable.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public bool Json => Has("--json");

    /// <summary>Flags given that no command recognises — reported rather than ignored, so a typo like
    /// <c>--jsonn</c> doesn't silently produce human-formatted output a script then fails to parse.</summary>
    public IEnumerable<string> UnknownFlags(params string[] known)
        => _flags.Concat(_values.Keys).Where(f => !known.Contains(f, StringComparer.Ordinal));
}
