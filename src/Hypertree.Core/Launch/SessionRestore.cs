namespace Hypertree.Launch;

/// <summary>
/// The OS-free half of session restore: decide which of a desktop's saved apps still need launching. Kept
/// out of the App's switch/launch orchestration so the "don't duplicate what's already open" rule is
/// unit-testable — the counterpart to <see cref="SessionCapture"/> on the way back in.
/// </summary>
public static class SessionRestore
{
    /// <summary>
    /// Which of the <paramref name="saved"/> apps to relaunch, given the executable <paramref name="present"/>
    /// paths that already have a window on the target desktop — so restore <em>tops up</em> a desktop rather
    /// than duplicating what's open. Path match is case-insensitive; a blank present path (a window we
    /// couldn't resolve) is ignored. Saved order is preserved.
    /// </summary>
    public static IReadOnlyList<CapturedApp> ToLaunch(IEnumerable<CapturedApp> saved, IEnumerable<string> present)
    {
        var have = present
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return saved.Where(a => !have.Contains(a.Path)).ToList();
    }
}
