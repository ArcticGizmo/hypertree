using Avalonia.Threading;
using Hypertree.Desktops;

namespace Hypertree.App;

/// <summary>
/// The shipped <see cref="IDesktopWatcher"/>: ticks <see cref="DesktopPoll"/> on a dispatcher timer so
/// Hypertree notices a desktop switch it didn't make (Win+Ctrl+Arrow, Task View, another launcher).
/// </summary>
/// <remarks>
/// <para>A <see cref="DispatcherTimer"/> rather than a background thread because the poll reads the
/// desktop COM, whose RCWs are bound to the UI/STA thread. The read is one out-of-process call — measured
/// at ~99µs mean (207µs p99) on build 26200, allocating nothing — so at <see cref="Interval"/> it occupies
/// about 0.04% of one core and never comes close to costing a frame.</para>
///
/// <para>The interval trades latency against that cost, and latency is the only thing actually at stake:
/// a quarter second means the taskbar pill and the status file settle ~125ms after an external switch, on
/// average. Before this existed they didn't settle at all until Hypertree was next used, so anything in
/// this range is a strict improvement; a shell-notification subscription would make it instant, at the
/// price of owning an inbound per-build COM vtable (see <see cref="IDesktopWatcher"/>).</para>
/// </remarks>
internal sealed class PollingDesktopWatcher : IDesktopWatcher
{
    /// <summary>Poll cadence. See the remarks for why this number and not a smaller one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly DesktopPoll _poll;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public event Action<DesktopId>? CurrentChanged;

    public PollingDesktopWatcher(IDesktopController desktops)
    {
        _poll = new DesktopPoll(desktops);
        _poll.Changed += id => CurrentChanged?.Invoke(id);
        // Background priority: noticing an external switch is never more urgent than drawing the frame
        // the user is looking at.
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => _poll.Tick());
    }

    /// <summary>
    /// Adopt <paramref name="id"/> as the current desktop without reporting a change — called when
    /// Hypertree itself drove the switch, so our own navigation doesn't arrive back on the next tick
    /// looking like something external moved us.
    /// </summary>
    public void Acknowledge(DesktopId id) => _poll.Acknowledge(id);

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
