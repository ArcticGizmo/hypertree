namespace Hypertree.Platform;

/// <summary>
/// Adds and removes Hypertree's install directory on the user's shell search path, so <c>htree</c> — and
/// <c>hypertree</c> itself — resolve from any terminal. Driven from the installer's lifecycle hooks, not
/// from the running app. Behind a Core interface so the App never touches the registry directly and a
/// non-Windows head can supply its own mechanism (a symlink into a bin dir, or a shell profile edit).
/// </summary>
public interface IPathInstaller
{
    /// <summary>Whether the install directory is currently on the user's PATH.</summary>
    bool IsRegistered { get; }

    /// <summary>Put the install directory on the PATH. No-op if it's already there.</summary>
    void Register();

    /// <summary>Take it off again. No-op if it isn't there.</summary>
    void Unregister();
}

/// <summary>
/// The pure string handling behind <see cref="IPathInstaller"/>: adding and removing one directory from a
/// <c>;</c>-separated PATH value.
/// </summary>
/// <remarks>
/// Split out from the platform implementation so it can be tested without reading or writing the
/// developer's real PATH — which is the one piece of this feature that genuinely must not be got wrong,
/// since a bad edit follows the user into every terminal they open and is tedious to trace back.
/// <para>
/// Both commands live in the same install directory, so a single entry exposes both binaries; there is
/// deliberately no per-executable registration.
/// </para>
/// </remarks>
public static class PathEntries
{
    /// <summary>
    /// <paramref name="pathVar"/> with <paramref name="dir"/> appended, or null when it's already present
    /// and nothing needs writing. Returning null rather than an identical string is what lets the caller
    /// skip the write entirely — and so avoid rewriting a PATH it didn't need to touch.
    /// </summary>
    public static string? Add(string? pathVar, string dir)
    {
        dir = Normalise(dir);
        if (dir.Length == 0) return null;

        string existing = pathVar ?? "";
        if (Split(existing).Any(p => Equal(p, dir))) return null;

        return existing.Trim().Length == 0 ? dir : existing.TrimEnd(';') + ";" + dir;
    }

    /// <summary>
    /// <paramref name="pathVar"/> with every entry matching <paramref name="dir"/> removed, or null when
    /// there was nothing to remove.
    /// </summary>
    /// <remarks>
    /// The null-when-unchanged rule matters more here than in <see cref="Add"/>. Rewriting the value
    /// unconditionally would re-join the user's PATH from our own split — quietly dropping empty entries
    /// and rewriting separators on an uninstall that should have been a no-op.
    /// </remarks>
    public static string? Remove(string? pathVar, string dir)
    {
        dir = Normalise(dir);
        if (dir.Length == 0 || string.IsNullOrEmpty(pathVar)) return null;

        string[] entries = pathVar.Split(';');
        var kept = entries.Where(e => !Equal(e, dir)).ToArray();
        return kept.Length == entries.Length ? null : string.Join(';', kept);
    }

    /// <summary>Whether <paramref name="dir"/> is already on <paramref name="pathVar"/>.</summary>
    public static bool Contains(string? pathVar, string dir)
        => Split(pathVar ?? "").Any(p => Equal(p, Normalise(dir)));

    private static IEnumerable<string> Split(string pathVar)
        => pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // PATH entries are conventionally written both with and without a trailing separator, and may be
    // quoted when they contain spaces — "C:\Program Files\x" and C:\Program Files\x are the same place.
    // Comparing without normalising would add a duplicate entry, or fail to remove one on uninstall.
    private static string Normalise(string dir)
        => dir.Trim().Trim('"').TrimEnd('\\', '/');

    private static bool Equal(string a, string b)
        => string.Equals(Normalise(a), b, StringComparison.OrdinalIgnoreCase);
}
