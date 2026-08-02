namespace Hypertree.Launch;

/// <summary>
/// Split a typed command line into a launch target and its arguments, so the loadout builder can accept a
/// natural single line (<c>code C:\proj</c>, <c>wt -d "C:\a b"</c>, <c>"C:\Program Files\x\app.exe" --flag</c>)
/// and store it as the <c>Target</c> + <c>Arguments</c> the shell launcher wants. OS-free and unit-tested.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Take the first token as the target (a leading quoted span is unwrapped, so a path with spaces stays
    /// whole), and everything after the gap as the arguments (verbatim, quotes and all). A blank input, or
    /// one that's only a target, yields empty arguments. Leading/trailing whitespace is trimmed.
    /// </summary>
    public static (string Target, string Arguments) Split(string? commandLine)
    {
        string s = (commandLine ?? "").Trim();
        if (s.Length == 0) return ("", "");

        int i;
        string target;
        if (s[0] is '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 0) { return (s.Substring(1), ""); } // unterminated quote → take the rest as the target
            target = s.Substring(1, close - 1);
            i = close + 1;
        }
        else
        {
            i = s.IndexOf(' ');
            if (i < 0) return (s, "");
            target = s.Substring(0, i);
        }

        string args = s.Substring(i).TrimStart();
        return (target, args);
    }

    /// <summary>Recompose a target + arguments back into a single line for display / editing — the inverse
    /// of <see cref="Split"/> for the common case (a target with spaces is re-quoted).</summary>
    public static string Join(string target, string? arguments)
    {
        string t = (target ?? "").Trim();
        if (t.Contains(' ') && !(t.StartsWith('"') && t.EndsWith('"'))) t = $"\"{t}\"";
        string a = (arguments ?? "").Trim();
        return a.Length == 0 ? t : $"{t} {a}";
    }
}
