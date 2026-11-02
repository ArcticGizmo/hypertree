using Hypertree.Desktops;

namespace Hypertree.Spatial;

/// <summary>
/// The animated pose of the hyperbolic map: every room's isometry, the background tiling's, and the spin, all
/// gliding from where they're shown toward a new layout. The background starts on the new anchor's old
/// position and glides home, so the anchor and the tiling under it move as one; any other room glides on its
/// own path, which is what lets a between-rows move re-hang the off-axis rooms smoothly. Pure state stepped by
/// the caller's clock, so it's testable without a timer.
/// </summary>
public sealed class HyperbolicMotion
{
    private const double Duration = 0.28;

    private Dictionary<DesktopId, Mobius> _from = new(), _to = new();
    private Mobius _bgFrom = Mobius.Identity;
    private double _spinFrom, _spinTo;
    private double _t = 1;

    /// <summary>True while a glide is under way.</summary>
    public bool Moving => _t < 1;

    /// <summary>Where the spin is heading — what the arrow keys should be read against, so a key pressed
    /// mid-turn already follows the new orientation.</summary>
    public double TargetSpin => _spinTo;

    /// <summary>Jump straight to a layout with no glide — on entering the view.</summary>
    public void Snap(IReadOnlyDictionary<DesktopId, Mobius> layout, double spin)
    {
        _to = new Dictionary<DesktopId, Mobius>(layout);
        _from = new Dictionary<DesktopId, Mobius>(layout);
        _bgFrom = Mobius.Identity;
        _spinFrom = _spinTo = spin;
        _t = 1;
    }

    /// <summary>Glide from whatever is showing now to <paramref name="layout"/> (anchored on
    /// <paramref name="anchor"/>, which ends at the centre) turned by <paramref name="spin"/>. A no-op when
    /// nothing would change, so calling it on every render is safe.</summary>
    public void Retarget(IReadOnlyDictionary<DesktopId, Mobius> layout, DesktopId? anchor, double spin)
    {
        if (Math.Abs(spin - _spinTo) < 1e-9 && SameLayout(layout)) return;

        var shown = new Dictionary<DesktopId, Mobius>(layout.Count);
        foreach ((DesktopId id, Mobius to) in layout)
            shown[id] = _to.ContainsKey(id) ? Room(id) : to; // a newcomer appears in place
        Mobius bg = anchor is { } a && _to.ContainsKey(a) && layout.ContainsKey(a) ? Room(a) : Background;

        _spinFrom = Spin;
        _spinTo = spin;
        _bgFrom = bg;
        _from = shown;
        _to = new Dictionary<DesktopId, Mobius>(layout);
        _t = 0;
    }

    /// <summary>Advance the glide by <paramref name="seconds"/>.</summary>
    public void Step(double seconds) => _t = Math.Min(1, _t + seconds / Duration);

    private double Eased => 1 - Math.Pow(1 - _t, 3);

    /// <summary>The spin shown now.</summary>
    public double Spin => _spinFrom + (_spinTo - _spinFrom) * Eased;

    /// <summary>A room's isometry shown now (before the spin), or the identity when it isn't laid out.</summary>
    public Mobius Room(DesktopId id) =>
        _to.TryGetValue(id, out Mobius to) ? Mobius.Lerp(_from.GetValueOrDefault(id, to), to, Eased) : Mobius.Identity;

    /// <summary>The background tiling's isometry shown now (before the spin).</summary>
    public Mobius Background => Mobius.Lerp(_bgFrom, Mobius.Identity, Eased);

    /// <summary>The rooms currently laid out.</summary>
    public IEnumerable<DesktopId> Rooms => _to.Keys;

    private bool SameLayout(IReadOnlyDictionary<DesktopId, Mobius> layout)
    {
        if (layout.Count != _to.Count) return false;
        foreach ((DesktopId id, Mobius m) in layout)
            if (!_to.TryGetValue(id, out Mobius o) || !Near(m, o)) return false;
        return true;
    }

    private static bool Near(Mobius x, Mobius y) =>
        (x.A - y.A).Magnitude < 1e-9 && (x.B - y.B).Magnitude < 1e-9;
}
