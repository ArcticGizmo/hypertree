namespace Hypertree.App;

/// <summary>
/// Keeps Hypertree to one running copy per Windows session. The first launch claims a named mutex and
/// listens on a named event; a later launch fails to claim the mutex, signals that event so the copy
/// already in the tray surfaces itself, and exits before any UI, hotkey or desktop-COM work happens.
/// </summary>
/// <remarks>
/// Two copies would fight over everything that matters: the global hotkeys are exclusive (the second
/// registration is refused by the OS, so half the chords would silently do nothing), and both would drive
/// the same virtual desktops from their own <c>_created</c> bookkeeping and persist conflicting state.
/// <para>
/// The names are <c>Local\</c>-scoped, i.e. per logon session — virtual desktops are per-session too, so
/// two users switched between on one machine each get their own Hypertree.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Hypertree.SingleInstance";
    private const string ActivateEventName = @"Local\Hypertree.Activate";

    private readonly Mutex _mutex;
    // Set by a later launch to ask us to surface. Auto-reset, so a signal that lands before the listener
    // thread starts (a second launch during our startup) is still consumed once it does.
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Claims the single-instance slot. Returns the claim when this process is the one Hypertree — keep it
    /// alive for the lifetime of the app. Returns <c>null</c> when a copy is already running (having asked
    /// it to surface first); the caller should exit immediately without starting the app.
    /// </summary>
    public static SingleInstance? Claim()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.Zero); }
        // The previous owner died without releasing (crash / kill). The slot is ours, and the mutex is now
        // held by this thread — an abandoned mutex is still acquired by the waiter that observes it.
        catch (AbandonedMutexException) { owned = true; }

        if (!owned)
        {
            mutex.Dispose();
            SignalRunningInstance();
            return null;
        }

        // Created only after winning the mutex, so exactly one process ever owns the activation event.
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName));
    }

    /// <summary>
    /// Run <paramref name="onActivated"/> whenever another launch asks us to surface. Fires on a background
    /// thread — marshal to the UI thread yourself. Call once, after the app is up.
    /// </summary>
    public void OnActivated(Action onActivated)
    {
        if (_listener is not null) return;
        _listener = new Thread(() =>
        {
            WaitHandle[] handles = { _activate, _stop };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { onActivated(); } catch { /* a rogue callback must never kill the listener */ }
            }
        })
        {
            IsBackground = true, // never holds up shutdown
            Name = "hypertree-activate",
        };
        _listener.Start();
    }

    // Best-effort poke at the copy already running. If the event isn't there yet (it's mid-startup, in the
    // microseconds between claiming the mutex and creating the event) we simply exit quietly — better a
    // launch that appears to do nothing than a second tray icon.
    private static void SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? handle)) return;
            using (handle) handle.Set();
        }
        catch { /* no rights / gone — nothing we can do, and nothing worth reporting */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        // Release explicitly rather than relying on process exit, so a restart (the Velopack update path)
        // can't be refused by our own lingering claim.
        try { _mutex.ReleaseMutex(); } catch { /* not owned — already released or abandoned */ }
        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
