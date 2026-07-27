namespace Hypertree.Desktops;

/// <summary>
/// Ambient notice that the OS moved to a different desktop <em>without us asking</em> — Win+Ctrl+Arrow,
/// Task View, another launcher jumping to one of its windows.
/// </summary>
/// <remarks>
/// Hypertree tracks a <em>cursor</em>, not the OS: before this existed, the navigation model (and the
/// taskbar pill it feeds) only re-anchored at the top of a navigation keystroke or when the map opened,
/// so an external switch left both stale until Hypertree was next used. Anything that publishes "where
/// am I now" to the outside — the status file, and through it the CLI and Perch — needs the answer to be
/// true continuously, not just after a hotkey.
/// <para>
/// Kept as a seam for the same reason <see cref="IDesktopController"/> is one. The shipped implementation
/// polls (see <see cref="DesktopPoll"/>); the alternative is registering an
/// <c>IVirtualDesktopNotification</c> callback with the shell, which is instant but a new class of
/// fragility — an <em>inbound</em> per-build vtable that the shell calls into, where a mismatch faults
/// inside a shell callback instead of returning a failed HRESULT we could shrug off. Measured on build
/// 26200, one poll costs ~99µs and allocates nothing, so at a quarter-second cadence the subscription's
/// entire saving is about 1.4 seconds of CPU per hour. If that trade ever changes, implement this
/// interface again and swap it at the composition root.
/// </para>
/// </remarks>
public interface IDesktopWatcher : IDisposable
{
    /// <summary>Raised when the OS's current desktop differs from the last one we observed. Carries the
    /// desktop now showing. Implementations must raise this on the app's UI/STA thread.</summary>
    event Action<DesktopId>? CurrentChanged;

    void Start();
    void Stop();
}

/// <summary>
/// The comparison half of a polling <see cref="IDesktopWatcher"/>, with no timer and no threading, so the
/// "did it change / is this our own move" logic is unit-testable against a fake controller. A host drives
/// <see cref="Tick"/> from whatever timer it owns.
/// </summary>
public sealed class DesktopPoll
{
    private readonly IDesktopController _desktops;

    // Null means "no baseline" — we have never managed to read the shell. Deliberately a nullable rather
    // than a sentinel id: an unreadable shell and a real desktop are different states, and conflating them
    // with Guid.Empty would make a legitimate reading indistinguishable from a failure.
    private DesktopId? _seen;

    /// <summary>Raised by <see cref="Tick"/> when the current desktop has changed since the last observation.</summary>
    public event Action<DesktopId>? Changed;

    public DesktopPoll(IDesktopController desktops)
    {
        _desktops = desktops;
        _seen = TryRead(); // seed, so the first tick reports a real move rather than the startup state
    }

    /// <summary>The desktop this poll last observed — what a change is measured against. Null before the
    /// shell has been read successfully even once.</summary>
    public DesktopId? Seen => _seen;

    /// <summary>
    /// Adopt <paramref name="id"/> as the last-observed desktop <em>without</em> raising
    /// <see cref="Changed"/>. Called when Hypertree itself drove the switch, so our own navigation doesn't
    /// come back round as an "external" change on the next tick.
    /// </summary>
    public void Acknowledge(DesktopId id) => _seen = id;

    /// <summary>
    /// Read the OS's current desktop and raise <see cref="Changed"/> if it moved. Returns true when it did.
    /// </summary>
    public bool Tick()
    {
        if (TryRead() is not { } now) return false; // unreadable this time — keep the baseline and retry later

        // First successful read after a failed start: adopt it silently rather than announcing startup as
        // though the user had switched desktop.
        if (_seen is null) { _seen = now; return false; }

        if (now == _seen) return false;
        _seen = now;
        Changed?.Invoke(now);
        return true;
    }

    // A COM failure (the shell restarting under us) reads as "no answer", never as a throw: a watcher that
    // died on one bad read would leave the app permanently stale, which is the thing it exists to fix.
    private DesktopId? TryRead()
    {
        try { return _desktops.Current; }
        catch { return null; }
    }
}
