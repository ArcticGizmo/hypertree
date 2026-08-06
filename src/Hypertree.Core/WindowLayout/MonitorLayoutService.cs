using Hypertree.Store;

namespace Hypertree.WindowLayout;

/// <summary>
/// Orchestrates monitor-layout capture and restore. Drives the two seams
/// (<see cref="IWindowLayoutController"/>, <see cref="IMonitorLayoutStore"/>) and holds no timer of its own:
/// a host ticks <see cref="Tick"/> from whatever timer it owns (the app uses a <c>DispatcherTimer</c>,
/// mirroring <see cref="Desktops.DesktopPoll"/>/<c>PollingDesktopWatcher</c>), which keeps every decision
/// here OS-free and unit-testable against fakes.
/// </summary>
/// <remarks>
/// The rule is symmetric and count-free: <b>leaving</b> a monitor set persists the arrangement we were
/// holding for it (so returning can restore it), and <b>arriving</b> at a set we have a capture for raises
/// <see cref="RestoreAvailable"/> (an <em>offer</em> — the design keeps restore opt-in per event). A change
/// must be seen on two consecutive ticks before it counts, which debounces the burst of transient readings
/// the shell emits mid-dock. While a set is steady, each tick refreshes the rolling "last good" capture, so
/// an undock saves the layout from <em>before</em> the shell crushed it, never the crushed result.
/// </remarks>
public sealed class MonitorLayoutService
{
    private readonly IWindowLayoutController _controller;
    private readonly IMonitorLayoutStore _store;

    private string _lastKey;                    // the settled current monitor set
    private MonitorLayoutSnapshot _rolling;     // the freshest capture of _lastKey's arrangement
    private string? _pendingKey;                // a changed key awaiting a second confirming tick (settle)

    /// <summary>Raised when we arrive at a monitor set we hold a saved layout for — an offer to restore it.
    /// The host decides how to surface it (a notification the user can click). May be handled on any thread
    /// the host ticks from; the app ticks on the UI thread.</summary>
    public event Action<MonitorLayoutSnapshot>? RestoreAvailable;

    /// <summary>Raised when leaving a set persisted its arrangement — the set key and how many windows were
    /// saved. Informational (the save is silent by design); the host may log it.</summary>
    public event Action<string, int>? LayoutSaved;

    public MonitorLayoutService(IWindowLayoutController controller, IMonitorLayoutStore store)
    {
        _controller = controller;
        _store = store;
        IReadOnlyList<MonitorRef> now = ReadMonitors();
        _lastKey = MonitorSet.Key(now);
        _rolling = TryCapture(now);             // seed silently — the current arrangement is the first "last good"
    }

    /// <summary>The monitor set we're currently settled on.</summary>
    public string CurrentSetKey => _lastKey;

    /// <summary>The monitors present now (fresh read) — for picker captions.</summary>
    public IReadOnlyList<MonitorRef> CurrentMonitors() => ReadMonitors();

    /// <summary>The saved layout for a set, or null — e.g. to re-offer the current set on demand.</summary>
    public MonitorLayoutSnapshot? AutoFor(string setKey) => _store.GetAuto(setKey);

    /// <summary>Every user-named layout.</summary>
    public IReadOnlyList<NamedMonitorLayout> NamedLayouts() => _store.Named();

    /// <summary>Capture the current arrangement (no side effects) — the manual "what's on screen now", and
    /// what the debug overlay compares against the saved layout for the monitors present now.</summary>
    public MonitorLayoutSnapshot CaptureNow() => _controller.Snapshot();

    /// <summary>
    /// Work out what restoring <paramref name="reference"/> would actually do against the live state: how
    /// many windows are on the wrong monitor (and so would move), and whether any of those is maximized and
    /// must cross monitors (the visibly-popping case the curtain hides). A window that's gone, or whose
    /// target monitor isn't present, isn't counted — it can't be placed.
    /// </summary>
    public RestorePlan PlanRestore(MonitorLayoutSnapshot reference)
    {
        MonitorLayoutSnapshot current = _controller.Snapshot();
        var currentMonitorOf = new Dictionary<long, string>();
        foreach (WindowPlacement w in current.Windows) currentMonitorOf[w.Hwnd] = w.MonitorStableId;
        var present = current.Monitors.Select(m => m.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int toMove = 0;
        bool curtain = false;
        foreach (WindowPlacement w in reference.Windows)
        {
            if (!currentMonitorOf.TryGetValue(w.Hwnd, out string? curMon)) continue; // window gone
            if (!present.Contains(w.MonitorStableId)) continue;                       // target monitor absent
            if (string.Equals(curMon, w.MonitorStableId, StringComparison.OrdinalIgnoreCase)) continue; // already right

            toMove++;
            if (w.Show == ShowState.Maximized) curtain = true;
        }
        return new RestorePlan(toMove, curtain);
    }

    /// <summary>Diagnostic restore: perform the moves and return a per-window trace (debug only). Refreshes
    /// the rolling capture afterwards, like <see cref="Restore"/>.</summary>
    public IReadOnlyList<WindowRestoreTrace> RestoreTraced(MonitorLayoutSnapshot snapshot)
    {
        IReadOnlyList<WindowRestoreTrace> trace = _controller.RestoreTraced(snapshot);
        _rolling = TryCapture(ReadMonitors());
        return trace;
    }

    /// <summary>Diagnostic: re-read one window's live geometry by handle (debug only).</summary>
    public WindowProbe Probe(long hwnd) => _controller.Probe(hwnd);

    /// <summary>Save the current arrangement under a name (the manual save path).</summary>
    public void SaveNamed(string name) => _store.SaveNamed(new NamedMonitorLayout(name, _controller.Snapshot()));

    /// <summary>Delete a named layout.</summary>
    public void DeleteNamed(string name) => _store.DeleteNamed(name);

    /// <summary>
    /// Restore an arrangement now. After it lands, the rolling capture is refreshed so the just-restored
    /// state becomes the new "last good" and the next tick doesn't treat it as a change to react to.
    /// </summary>
    public RestoreReport Restore(MonitorLayoutSnapshot snapshot)
    {
        RestoreReport report = _controller.Restore(snapshot);
        _rolling = TryCapture(ReadMonitors());
        return report;
    }

    /// <summary>
    /// One observation of the monitor topology. Drive it from a timer. Returns true when this tick acted on
    /// a settled change (saved and/or offered), for a host that wants to know something happened.
    /// </summary>
    public bool Tick()
    {
        IReadOnlyList<MonitorRef> now = ReadMonitors();
        if (now.Count == 0) return false;       // unreadable mid-transition — keep state, retry next tick
        string key = MonitorSet.Key(now);

        if (key == _lastKey)
        {
            _pendingKey = null;                 // any in-flight change reverted before settling
            _rolling = TryCapture(now);         // steady state — keep the "last good" fresh
            return false;
        }

        if (key != _pendingKey)                 // first sighting of a new set — wait one tick for it to settle
        {
            _pendingKey = key;
            return false;
        }

        // Settled on a new set. Persist what we're leaving, then offer what we're arriving at.
        _pendingKey = null;

        // Leaving a set saves its arrangement so returning can restore it — but only for multi-monitor sets.
        // A single-screen arrangement has nothing to spread back out, so there's nothing worth keeping.
        if (_rolling.Monitors.Count > 1 && _rolling.Windows.Count > 0)
        {
            _store.PutAuto(_rolling);
            LayoutSaved?.Invoke(_lastKey, _rolling.Windows.Count);
        }

        MonitorLayoutSnapshot? known = _store.GetAuto(key);

        _lastKey = key;
        _rolling = TryCapture(now);             // the arriving arrangement becomes the new rolling baseline

        // Offer the saved layout on arrival — but never when returning to a single screen, where every window
        // is forced onto the one monitor anyway, so a restore would move nothing meaningful.
        if (known is not null && now.Count > 1) RestoreAvailable?.Invoke(known);
        return true;
    }

    private IReadOnlyList<MonitorRef> ReadMonitors()
    {
        try { return _controller.Monitors(); }
        catch { return Array.Empty<MonitorRef>(); }  // a shell mid-reconfigure reads as "no answer", never a throw
    }

    private MonitorLayoutSnapshot TryCapture(IReadOnlyList<MonitorRef> now)
    {
        try { return _controller.Snapshot(); }
        catch { return new MonitorLayoutSnapshot(MonitorSet.Key(now), now, Array.Empty<WindowPlacement>()); }
    }
}
