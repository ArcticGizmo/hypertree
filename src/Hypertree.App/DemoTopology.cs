using Hypertree.Desktops;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// M1's hard-coded scope map. Deliberately NON-DESTRUCTIVE: it imposes a 2-D Model-P topology over
/// the user's EXISTING desktops (creates and removes nothing), so launching Hypertree to feel the
/// dive/surface/resume interaction has zero side effects. Real git-worktree-driven topology + desktop
/// provisioning arrive in M2.
///
/// Layout (using the first existing desktops, in OS order):
///   • anchor 0 "Main" — with scope "feat-123" over the next up-to-3 desktops (SPA / API / Mobile)
///   • anchor 1 "Side" — the 5th desktop, if present (a scope-less anchor, to feel the top row)
/// Degrades gracefully when fewer desktops exist.
/// </summary>
internal static class DemoTopology
{
    private static readonly string[] ScopeLabels = { "SPA", "API", "Mobile" };

    public static Topology Build(IReadOnlyList<DesktopInfo> existing)
    {
        if (existing.Count == 0) throw new InvalidOperationException("No virtual desktops present.");

        var anchors = new List<Anchor>();

        // Scope for the first anchor: desktops [1..3] of the existing set.
        var scopeDesktops = new List<DesktopRef>();
        for (int i = 1; i <= ScopeLabels.Length && i < existing.Count; i++)
            scopeDesktops.Add(new DesktopRef(existing[i].Id, ScopeLabels[i - 1]));

        Scope? scope = scopeDesktops.Count > 0 ? new Scope("feat-123", scopeDesktops) : null;
        anchors.Add(new Anchor(new DesktopRef(existing[0].Id, "Main"), scope));

        // A second, scope-less anchor so the top row has somewhere to move to.
        int sideIndex = ScopeLabels.Length + 1; // 4
        if (existing.Count > sideIndex)
            anchors.Add(new Anchor(new DesktopRef(existing[sideIndex].Id, "Side")));

        return new Topology(anchors);
    }
}
