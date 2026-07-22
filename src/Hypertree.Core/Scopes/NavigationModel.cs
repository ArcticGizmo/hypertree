using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state (PLAN.md §3). Tracks where you are on the 2-D map — which anchor column
/// on the day-to-day row, and whether you've dived into that anchor's scope — and translates the
/// four <see cref="NavAction"/>s into desktop switches via <see cref="IDesktopController"/>.
///
/// It holds no Win32 and no UI, so the entire *feel* of the model (dive/surface/resume/edges) is
/// unit-testable against a fake controller. The locked sub-decisions live here:
///   • Surface always returns to the current scope's anchor — the entry point (§3.2).
///   • Dive resumes the scope's last-used desktop, not its first (§3.3).
///   • Edges clamp; Surface at day-to-day and Dive on a scope-less anchor are no-ops (§5).
/// </summary>
public sealed class NavigationModel
{
    private readonly Topology _topology;
    private readonly IDesktopController _desktops;

    private int _anchorIndex;   // current column on the day-to-day row; also the owning anchor when in a scope
    private bool _inScope;      // false = on the day-to-day row, true = inside _anchorIndex's scope
    private DesktopId _target;  // the desktop the model believes is current (what it last switched to)

    /// <summary>Raised after an action that actually changed location (drives the HUD flash).</summary>
    public event Action? Changed;

    public NavigationModel(Topology topology, IDesktopController desktops)
    {
        _topology = topology;
        _desktops = desktops;

        // Start on the day-to-day row, on whichever anchor the OS is currently showing (so launching
        // Hypertree doesn't teleport you); fall back to the first anchor. Does NOT switch on init.
        DesktopId current = desktops.Current;
        _anchorIndex = 0;
        for (int i = 0; i < topology.Anchors.Count; i++)
        {
            if (topology.Anchors[i].Desktop.Id == current) { _anchorIndex = i; break; }
        }
        _target = CurrentDesktop().Id;
    }

    /// <summary>The anchor whose column we're on (and whose scope we're in, when dived).</summary>
    private Anchor CurrentAnchor => _topology.Anchors[_anchorIndex];

    /// <summary>The desktop the model considers current, given level + position.</summary>
    private DesktopRef CurrentDesktop()
        => _inScope ? CurrentAnchor.Scope!.Desktops[CurrentAnchor.Scope!.LastUsedIndex]
                    : CurrentAnchor.Desktop;

    /// <summary>Where the user is, formatted for the HUD (source of truth — PLAN.md §3.4).</summary>
    public NavLocation Location
    {
        get
        {
            if (_inScope)
            {
                Scope s = CurrentAnchor.Scope!;
                return new NavLocation(true, s.Name, s.Desktops[s.LastUsedIndex].Label, s.LastUsedIndex + 1, s.Desktops.Count);
            }
            return new NavLocation(false, null, CurrentAnchor.Desktop.Label, _anchorIndex + 1, _topology.Anchors.Count);
        }
    }

    /// <summary>Apply a navigation intent. Returns true if location changed (and a switch was issued).</summary>
    public bool Apply(NavAction action)
    {
        switch (action)
        {
            case NavAction.MoveLeft:  return Move(-1);
            case NavAction.MoveRight: return Move(+1);
            case NavAction.Dive:      return Dive();
            case NavAction.Surface:   return Surface();
            default:                  return false;
        }
    }

    private bool Move(int delta)
    {
        if (_inScope)
        {
            Scope s = CurrentAnchor.Scope!;
            int next = Math.Clamp(s.LastUsedIndex + delta, 0, s.Desktops.Count - 1);
            if (next == s.LastUsedIndex) return false;   // at an edge — clamp, no switch
            s.LastUsedIndex = next;                       // remember position for resume
        }
        else
        {
            int next = Math.Clamp(_anchorIndex + delta, 0, _topology.Anchors.Count - 1);
            if (next == _anchorIndex) return false;
            _anchorIndex = next;
        }
        return Commit();
    }

    private bool Dive()
    {
        if (_inScope) return false;                 // already inside a scope
        if (CurrentAnchor.Scope is null) return false; // scope-less anchor — no-op (M2: offer to create)
        _inScope = true;                            // position stays at the scope's LastUsedIndex → resume
        return Commit();
    }

    private bool Surface()
    {
        if (!_inScope) return false;                // already on the day-to-day row
        _inScope = false;                           // land back on the anchor — the entry point
        return Commit();
    }

    /// <summary>Switch to the model's current desktop if it differs, and signal the change.</summary>
    private bool Commit()
    {
        DesktopId id = CurrentDesktop().Id;
        if (id == _target) return false;
        _target = id;
        _desktops.SwitchTo(id);
        Changed?.Invoke();
        return true;
    }
}

/// <summary>
/// A HUD-ready snapshot of the current location. <see cref="Format"/> renders the source-of-truth
/// readout, e.g. <c>▸ feat-123 · API (2/3)</c> inside a scope, or <c>Web (1/3)</c> on the top row.
/// </summary>
public sealed record NavLocation(bool InScope, string? ScopeName, string DesktopLabel, int Position, int Count)
{
    public string Format()
        => InScope
            ? $"▸ {ScopeName} · {DesktopLabel} ({Position}/{Count})"
            : $"{DesktopLabel} ({Position}/{Count})";
}
