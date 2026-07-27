using Avalonia.Threading;
using Hypertree.Scopes;
using Hypertree.Status;

namespace Hypertree.App.Status;

/// <summary>
/// Keeps <c>%APPDATA%\hypertree\status.json</c> in step with the navigation model, so anything outside the
/// process — <c>htree</c>, a shell prompt, Perch's overlay strip — can see the layout and where the cursor
/// is without asking the tray.
/// </summary>
/// <remarks>
/// <para><b>Why debounced.</b> The model raises <c>Changed</c> on every committed navigation, and
/// navigation is a held-modifier gesture: running down a branch with Ctrl+Alt+Right fires one change per
/// desktop. Writing the file on each would put a burst of disk writes — and a burst of watcher callbacks
/// in every reader — behind a single human keystroke sequence. Coalescing on a short trailing delay costs
/// the reader nothing (the end state is what matters) and turns a gesture into one write.</para>
///
/// <para>The delay is a trailing one, not a leading one: during a gesture the intermediate positions are
/// noise, and it's the desktop you <em>land</em> on that readers want.</para>
/// </remarks>
internal sealed class StatusPublisher : IDisposable
{
    /// <summary>Trailing coalesce window. Long enough to swallow a fast arrow gesture, short enough that
    /// a reader watching the file feels it as immediate.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly NavigationModel _model;
    private readonly string _version;
    private readonly string? _cliPath;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public StatusPublisher(NavigationModel model, string version, string? cliPath)
    {
        _model = model;
        _version = version;
        _cliPath = cliPath;
        _timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => Flush());
    }

    /// <summary>Note that the model changed; the file is written once the burst settles.</summary>
    public void Schedule()
    {
        if (_disposed) return;
        _timer.Stop();  // restart the window — this is a trailing debounce, not a fixed-rate flush
        _timer.Start();
    }

    /// <summary>Write immediately, skipping the debounce. For startup and shutdown, where there is no
    /// burst to coalesce and a reader is better served by the file simply being correct.</summary>
    public void PublishNow()
    {
        if (_disposed) return;
        _timer.Stop();
        Flush();
    }

    private void Flush()
    {
        _timer.Stop();
        try
        {
            StatusSnapshot snapshot = _model.BuildStatus();
            snapshot.Version = _version;
            snapshot.Pid = Environment.ProcessId;
            snapshot.Cli = _cliPath;
            StatusFile.Write(snapshot);
        }
        catch { /* best-effort — publishing status must never disturb the tray */ }
    }

    /// <summary>Remove the file, so nothing reports a live tray after we've gone.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        StatusFile.Delete();
    }
}
