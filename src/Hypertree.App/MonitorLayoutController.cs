using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

/// <summary>
/// Monitor-layout restore (the physical-screen axis, see docs/design/monitor-layout-restore.md): owns the
/// <see cref="MonitorLayoutService"/> and its background poll, snapshots windows-per-monitor on undock,
/// and offers to put them back on redock via a clickable notification. Also hosts the dev-only debug/trace
/// overlay. Best-effort: if the platform can't provide a window-layout controller, tracking simply stays
/// off (<see cref="IsAvailable"/> is false) rather than failing startup.
/// </summary>
/// <remarks>
/// The notifier is read through an accessor rather than captured, because it is built after this controller
/// is started (see App.Startup); reading it live preserves the original "<c>_notifier?.Show(...)</c> at
/// call time" behaviour.
/// </remarks>
public sealed class MonitorLayoutController : IDisposable
{
    // The notification-click action string the "restore your layout?" toast carries. The app shell wires
    // INotifier.Activated and routes this action back to RestoreCurrentSetLayout.
    public const string RestoreAction = "restore-monitor-layout";
    // The redock offer and its follow-up share one key, so the confirmation replaces the offer rather than
    // stacking beside it.
    private const string NoticeKey = "monitor-layout";

    private readonly IDesktopController _desktops;
    private readonly IForegroundActivator _activator;
    private readonly Func<INotifier?> _notifier;

    private MonitorLayoutService? _layout;
    private DispatcherTimer? _layoutTimer;
    private MonitorDebugWindow? _debugWindow;

    public MonitorLayoutController(IDesktopController desktops, IForegroundActivator activator, Func<INotifier?> notifier)
    {
        _desktops = desktops;
        _activator = activator;
        _notifier = notifier;
    }

    /// <summary>True once monitor tracking is running (the platform provided a window-layout controller).</summary>
    public bool IsAvailable => _layout is not null;

    /// <summary>
    /// Start tracking the physical monitor topology. A background-priority poll (mirroring
    /// <see cref="PollingDesktopWatcher"/> — dock/undock is not latency-sensitive and the read is cheap)
    /// drives <see cref="MonitorLayoutService.Tick"/>; the service saves the arrangement on undock and
    /// raises <see cref="MonitorLayoutService.RestoreAvailable"/> on redock, which we surface as a
    /// clickable notification. Best-effort: if the platform can't provide the controller, layout tracking
    /// simply stays off rather than failing startup.
    /// </summary>
    public void Start()
    {
        IWindowLayoutController controller;
        try { controller = PlatformServices.CreateWindowLayoutController(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Hypertree could not start monitor-layout tracking: {ex.Message}");
            return;
        }

        _layout = new MonitorLayoutService(controller, new FileMonitorLayoutStore());
        _layout.RestoreAvailable += OnRestoreAvailable;

        // 2s cadence: a change must persist two ticks to count (~4s effective settle), which lets the shell
        // finish reshuffling before we read or restore — the debounce the spike proved is needed.
        _layoutTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _layout!.Tick());
        _layoutTimer.Start();
    }

    // Redock to a set we have a capture for. The service already debounced; we surface an offer the user
    // clicks to apply directly (restore is opt-in per event, never an automatic teleport). Only prompt when
    // windows are actually out of place, and say how many will move — not the raw snapshot size.
    private void OnRestoreAvailable(MonitorLayoutSnapshot snapshot) => OnUi(() =>
    {
        if (_layout is null) return;
        int toMove = _layout.PlanRestore(snapshot).ToMove;
        if (toMove == 0) return; // everything already where it belongs — nothing to offer

        _notifier()?.Show(
            "Restore your window layout?",
            $"You're back on {snapshot.Monitors.Count} monitors. Click to move {toMove} window{(toMove == 1 ? "" : "s")} back into place.",
            action: RestoreAction, replaces: NoticeKey);
    });

    private void DoRestoreMonitorLayout(MonitorLayoutSnapshot snapshot)
    {
        if (_layout is null) return;

        // Only curtain the restore when the maximize-cross-monitor trick is in play — that's the move that
        // visibly pops; a plain move needs no cover. Report through the same silent confirmation either way.
        if (_layout.PlanRestore(snapshot).NeedsCurtain)
            RestoreCurtain.Run(_layout.CurrentMonitors(), "Restoring positions…", () => ApplyRestore(snapshot));
        else
            ApplyRestore(snapshot);
    }

    private void ApplyRestore(MonitorLayoutSnapshot snapshot)
    {
        if (_layout is null) return;
        RestoreReport r = _layout.Restore(snapshot);
        int skipped = r.Gone + r.MonitorMissing + r.Refused;
        _notifier()?.Show(
            "Layout restored",
            $"{r.Placed} window{(r.Placed == 1 ? "" : "s")} put back"
                + (skipped > 0 ? $" · {skipped} skipped ({r.Gone} closed, {r.MonitorMissing} off-screen, {r.Refused} refused)" : "."),
            silent: true, replaces: NoticeKey);
    }

    // The debug overlay: a scrollable list of virtual desktops, each with a box per monitor listing the
    // windows on it, every window showing the monitor it's on and the one the saved layout wants. Combines
    // the two axes — desktop grouping from IDesktopController, current/wanted monitor from the layout
    // snapshot vs the saved capture for the monitors present now. Rough by intent; it's a debug aid.
    public void OpenDebugOverlay()
    {
        if (_layout is null) return;

        // Offer restore when there's a saved layout for the set we're on. The action re-resolves the layout
        // at click time and does not close the window — it re-polls so you can watch where windows land.
        bool hasSaved = _layout.AutoFor(_layout.CaptureNow().SetKey) is not null;
        Action? onRestore = hasSaved ? RestoreCurrentSetLayout : null;
        Action? onTrace = hasSaved ? TraceRestoreToFile : null;

        _debugWindow?.Close();
        _debugWindow = new MonitorDebugWindow(BuildMonitorDebugView, _activator, onRestore, onTrace);
        _debugWindow.Closed += (_, _) => _debugWindow = null;
        _debugWindow.Show();
        _debugWindow.TakeFocus();
    }

    // Rebuild the overlay's view data from the live state — called on open and on every refresh/restore.
    private (IReadOnlyList<DebugDesktopRow> desktops, string subtitle) BuildMonitorDebugView()
    {
        if (_layout is null)
            return (Array.Empty<DebugDesktopRow>(), "unavailable");

        MonitorLayoutSnapshot snap = _layout.CaptureNow();
        MonitorLayoutSnapshot? reference = _layout.AutoFor(snap.SetKey);

        Dictionary<long, WindowPlacement> curByHwnd = snap.Windows.ToDictionary(w => w.Hwnd);
        Dictionary<long, WindowPlacement> wantByHwnd =
            reference?.Windows.ToDictionary(w => w.Hwnd) ?? new Dictionary<long, WindowPlacement>();
        var monName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorRef m in snap.Monitors) monName[m.StableId] = m.Friendly;
        string Name(string id) => id.Length > 0 && monName.TryGetValue(id, out string? f) && f.Length > 0 ? f : (id.Length > 0 ? id : "?");

        var desktops = new List<DebugDesktopRow>();
        foreach (DesktopInfo d in _desktops.List())
        {
            var byMon = new Dictionary<string, List<DebugWindowRow>>();
            foreach (WindowInfo w in _desktops.WindowsOn(d.Id))
            {
                long hwnd = (long)w.Hwnd;
                string curId = curByHwnd.TryGetValue(hwnd, out WindowPlacement? cp) ? cp.MonitorStableId : "";
                string? wantId = wantByHwnd.TryGetValue(hwnd, out WindowPlacement? wp) ? wp.MonitorStableId : null;
                string boxKey = curId.Length > 0 ? curId : "?";
                if (!byMon.TryGetValue(boxKey, out var list)) byMon[boxKey] = list = new List<DebugWindowRow>();
                list.Add(new DebugWindowRow(w.Title, Name(curId), wantId is null ? null : Name(wantId)));
            }

            var boxes = new List<DebugMonitorBox>();
            foreach (MonitorRef m in snap.Monitors) // monitors in a stable order; empty ones shown too
                boxes.Add(new DebugMonitorBox(Name(m.StableId),
                    byMon.TryGetValue(m.StableId, out var list) ? list : new List<DebugWindowRow>()));
            if (byMon.TryGetValue("?", out var unknown)) // windows we couldn't attribute to a present monitor
                boxes.Add(new DebugMonitorBox("(unknown)", unknown));

            string label = string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name;
            desktops.Add(new DebugDesktopRow(label, boxes));
        }

        string subtitle = reference is null
            ? $"set {snap.SetKey} · {snap.Monitors.Count} monitors · no saved layout for this set — current placement only"
            : $"set {snap.SetKey} · {snap.Monitors.Count} monitors · vs saved layout ({reference.Windows.Count} windows)";
        return (desktops, subtitle);
    }

    // Restore the saved layout for whatever monitor set we're on now (re-resolved at call time, so the debug
    // overlay's Restore button always acts on the current set). Also the target of the "restore your layout?"
    // notification click, routed here by the app shell.
    public void RestoreCurrentSetLayout()
    {
        if (_layout is null) return;
        if (_layout.AutoFor(_layout.CaptureNow().SetKey) is { } reference) DoRestoreMonitorLayout(reference);
    }

    // Debug: run a traced restore of the current set's saved layout and write a detailed per-window report
    // (before/target/after rectangles, SetWindowPlacement result + error). A window that moves then snaps
    // back only shows up over time, so we re-probe every traced window after a beat and append where it
    // actually settled — the signature of an app (Slack/Electron) overriding our move. Report opens on write.
    private void TraceRestoreToFile()
    {
        if (_layout is null) return;
        MonitorLayoutSnapshot snap = _layout.CaptureNow();
        if (_layout.AutoFor(snap.SetKey) is not { } reference)
        {
            _notifier()?.Show("Restore trace", "No saved layout for these monitors to trace.", silent: true,
                replaces: NoticeKey);
            return;
        }

        IReadOnlyList<WindowRestoreTrace> trace = _layout.RestoreTraced(reference);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Monitor restore trace — set {snap.SetKey}");
        sb.AppendLine($"{snap.Monitors.Count} monitor(s) present:");
        foreach (MonitorRef m in snap.Monitors)
            sb.AppendLine($"  {(m.IsPrimary ? "*" : " ")} {m.Friendly} [{m.StableId}]  {m.Bounds.Width}x{m.Bounds.Height} @ {m.Bounds.Left},{m.Bounds.Top}  dpi {m.Dpi}");
        sb.AppendLine($"Saved layout: {reference.Windows.Count} window(s).");
        sb.AppendLine();
        sb.AppendLine("── Immediate (right after SetWindowPlacement) ──");
        static string R(Recti r) => $"{r.Left},{r.Top} {r.Width}x{r.Height}";
        foreach (WindowRestoreTrace t in trace)
        {
            sb.AppendLine($"[{t.Outcome}]  \"{t.Title}\"");
            sb.AppendLine($"    hwnd=0x{t.Hwnd:X}  proc={t.ProcessName}  class={t.ClassName}");
            sb.AppendLine($"    wants: {t.WantedMonitor} ({(t.MonitorPresent ? "present" : "ABSENT")})  show={t.WantedShow}");
            sb.AppendLine($"    before: {R(t.BeforeRect)}   target: {R(t.TargetRect)}   after: {R(t.AfterRect)}");
            sb.AppendLine($"    SetWindowPlacement={t.SetResult}  lastError={t.LastError}");
            bool movedImmediately = t.BeforeRect != t.AfterRect;
            sb.AppendLine($"    moved-immediately={movedImmediately}");
            sb.AppendLine();
        }

        // Re-probe after the shell settles, to catch a move that reverted.
        DispatcherTimer.RunOnce(() =>
        {
            if (_layout is null) return;
            sb.AppendLine("── Settled (≈800ms later) ──");
            foreach (WindowRestoreTrace t in trace)
            {
                WindowProbe p = _layout.Probe(t.Hwnd);
                if (!p.Exists) { sb.AppendLine($"    \"{t.Title}\" — window gone"); continue; }
                bool onWanted = string.Equals(p.MonitorFriendly, t.WantedMonitor, StringComparison.OrdinalIgnoreCase);
                bool reverted = t.SetResult && t.BeforeRect != t.AfterRect && p.Rect == t.BeforeRect;
                sb.AppendLine($"    \"{t.Title}\"  proc={t.ProcessName}  now on {p.MonitorFriendly} [{R(p.Rect)}] show={p.Show}"
                    + $"  {(onWanted ? "OK" : "NOT on wanted " + t.WantedMonitor)}{(reverted ? "  ← SNAPPED BACK to start" : "")}");
            }

            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
            string path = System.IO.Path.Combine(dir, "restore-trace.txt");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, sb.ToString());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _notifier()?.Show("Restore trace", $"Couldn't open the report ({ex.Message}). It's at {path}.",
                    silent: true, replaces: NoticeKey);
            }

            _debugWindow?.Focus();
        }, TimeSpan.FromMilliseconds(800));
    }

    // Run on the UI thread. RestoreAvailable can be raised off the poll's own tick context; every field
    // touch and notifier call is marshalled back through here (mirrors App.OnUi).
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        _layoutTimer?.Stop();
        _debugWindow?.Close();
    }
}
