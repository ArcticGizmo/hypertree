using Hypertree.Status;

namespace Hypertree.Cli;

/// <summary>What a user-typed target resolved to, or why it didn't.</summary>
internal sealed record TargetResolution(Guid? BranchId, int? Desktop, string? Error, bool Ambiguous = false)
{
    public bool Ok => Error is null;

    public static TargetResolution Found(Guid? branchId, int? desktop) => new(branchId, desktop, null);
    public static TargetResolution Fail(string error, bool ambiguous = false) => new(null, null, error, ambiguous);
}

/// <summary>
/// Turns what a human types — <c>perch</c>, <c>perch/docs</c>, <c>main</c>, <c>notes/2</c> — into the
/// unambiguous address the tray accepts.
/// </summary>
/// <remarks>
/// <para>Resolution happens here, in the client, rather than in the tray, for two reasons. A name can be
/// ambiguous (branch names are not unique or enforced), and the only place that's usefully reportable is
/// next to the person who typed it. And resolving to a stable id before sending means the request can't
/// be misapplied if the user reorders the stack between the read and the jump — which addressing by
/// position would allow.</para>
///
/// <para>Matching is deliberately forgiving in the order a person would expect: an exact name, then a
/// case-insensitive one, then a unique prefix. The prefix step is what makes the tool pleasant to type —
/// <c>htree goto pe</c> — while still refusing rather than guessing when a prefix hits more than one row.</para>
/// </remarks>
internal static class Targets
{
    /// <summary>The literal that addresses the main timeline. Not a branch, and it has no id.</summary>
    public const string Main = "main";

    /// <summary>
    /// Resolve <paramref name="target"/> against the published layout. Accepts <c>row</c> or
    /// <c>row/desktop</c>; a desktop is matched by label first, then as a 1-based position.
    /// </summary>
    public static TargetResolution Resolve(StatusSnapshot status, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return TargetResolution.Fail("Empty target.");

        // Split on the FIRST slash. A row name is a single token, but a desktop label is free text and may
        // well contain a slash ("ui/spec"), so everything after the first separator belongs to the label.
        string rowPart = target, desktopPart = "";
        int slash = target.IndexOf('/');
        if (slash > 0 && slash < target.Length - 1)
        {
            rowPart = target[..slash];
            desktopPart = target[(slash + 1)..];
        }

        TargetResolution row = ResolveRow(status, rowPart, out StatusRow? matched);
        if (!row.Ok || matched is null) return row;
        if (desktopPart.Length == 0) return row; // bare row — the tray uses its resume point

        int? desktop = ResolveDesktop(matched, desktopPart);
        if (desktop is null)
            return TargetResolution.Fail(
                $"'{matched.Name}' has no desktop '{desktopPart}'. It has {Describe(matched)}.");

        return TargetResolution.Found(row.BranchId, desktop);
    }

    private static TargetResolution ResolveRow(StatusSnapshot status, string name, out StatusRow? matched)
    {
        matched = null;

        // An explicit id always wins and is never ambiguous — this is the form other tools use.
        if (Guid.TryParse(name, out Guid id))
        {
            matched = status.Rows.FirstOrDefault(r => r.Id == id);
            return matched is null
                ? TargetResolution.Fail($"No branch with id {id}.")
                : TargetResolution.Found(id, null);
        }

        if (string.Equals(name, Main, StringComparison.OrdinalIgnoreCase))
        {
            matched = status.Rows.FirstOrDefault(r => r.IsMain);
            return matched is null
                ? TargetResolution.Fail("There is no main timeline.")
                : TargetResolution.Found(null, null);
        }

        List<StatusRow> hits = status.Rows.Where(r => r.Name == name).ToList();
        if (hits.Count == 0)
            hits = status.Rows.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
            hits = status.Rows.Where(r => r.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 0)
            return TargetResolution.Fail($"No branch matching '{name}'. Try: htree list");

        if (hits.Count > 1)
        {
            // Names aren't unique, so say exactly which ones collided and give the ids that disambiguate.
            string detail = string.Join("\n", hits.Select(r => $"  {r.Name,-16} {IdOf(r)}"));
            return TargetResolution.Fail(
                $"'{name}' matches {hits.Count} rows — address one by id:\n{detail}", ambiguous: true);
        }

        matched = hits[0];
        return TargetResolution.Found(matched.IsMain ? null : matched.Id, null);
    }

    // Label first so a desktop literally named "2" is reachable as "2"; only then treat a number as a
    // position. Positions are 1-based, matching what `htree list --all` prints.
    private static int? ResolveDesktop(StatusRow row, string desktop)
    {
        int exact = row.Desktops.FindIndex(d => d.Label == desktop);
        if (exact >= 0) return exact;

        int loose = row.Desktops.FindIndex(d => string.Equals(d.Label, desktop, StringComparison.OrdinalIgnoreCase));
        if (loose >= 0) return loose;

        if (int.TryParse(desktop, out int n) && n >= 1 && n <= row.Desktops.Count) return n - 1;

        int prefix = row.Desktops.FindIndex(d => d.Label.StartsWith(desktop, StringComparison.OrdinalIgnoreCase));
        return prefix >= 0 ? prefix : null;
    }

    private static string Describe(StatusRow row)
        => row.Desktops.Count == 0
            ? "none"
            : string.Join(", ", row.Desktops.Select((d, i) => $"{i + 1}:{d.Label}"));

    /// <summary>A row's addressable id — its GUID, or the literal <c>main</c>.</summary>
    public static string IdOf(StatusRow row) => row.IsMain ? Main : row.Id?.ToString() ?? Main;
}
