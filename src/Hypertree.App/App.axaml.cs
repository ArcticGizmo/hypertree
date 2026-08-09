using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Ipc;
using Hypertree.App.Status;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Changelog;
using Hypertree.Desktops;
using Hypertree.Ipc;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

/// <summary>
/// The tray/HUD/hotkey shell. Builds the desktop controller and navigation model (top row = the
/// existing unbranched desktops; branches are created at runtime), wires the Ctrl+Alt+Arrow nav hotkeys
/// and the Ctrl+Alt+P command palette, and flashes the board on navigation. Tray-only, outlives its
/// windows (ShutdownMode.OnExplicitShutdown).
/// </summary>
public sealed partial class App : Application
{
    // Which NavAction each navigation command drives. The chords themselves are resolved from settings
    // (defaults: Ctrl+Alt+Arrow — the M0 layer, since Win+Ctrl+Arrow is the native desktop switch) and
    // are rebindable in the settings window.
    private static readonly IReadOnlyDictionary<HotkeyCommand, NavAction> NavCommands =
        new Dictionary<HotkeyCommand, NavAction>
        {
            [HotkeyCommand.Dive]      = NavAction.Dive,
            [HotkeyCommand.Surface]   = NavAction.Surface,
            [HotkeyCommand.MoveLeft]  = NavAction.MoveLeft,
            [HotkeyCommand.MoveRight] = NavAction.MoveRight,
        };

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree created (for branches). Only these are ever torn down — the top row is the
    // user's own desktops and must never be removed.
    private readonly HashSet<Guid> _created = new();
    private IDesktopController? _desktops;
    private IForegroundActivator? _activator;
    private IStartupManager? _startup;
    // App-launcher services (Ctrl+Alt+O): installed-app discovery, shell launching, and icon extraction.
    // See App.Launcher.cs for the overlay that uses them.
    private Hypertree.Launch.IAppCatalog? _appCatalog;
    private Hypertree.Launch.IAppLauncher? _appLauncher;
    private Hypertree.Launch.IAppIconProvider? _appIcons;
    private NavigationModel? _model;
    private HudWindow? _hud;
    // The spatial map — the app's single map and manage-desktops surface. Its extra facts — group colours,
    // room positions — live in their own spatial.json, apart from navigation state.
    // See docs/design/spatial-map-plan.md.
    private SpatialOverlay? _spatialOverlay;
    private ISpatialStore? _spatialStore;
    private SpatialState _spatial = new();
    // One camera shared by the flash and the interactive map, so navigating with the map closed leaves it
    // framed where the map opens, and the two never disagree about where the map sits. Reframed on a theme
    // switch (metrics change). See docs/design/scene-camera.md.
    private readonly MapCamera _mapCamera = new();
    private DesktopId? _moveOrigin; // where the current move flow started, for cancel/restore
    private TaskbarLabel? _taskbarLabel;
    private SwitcherWindow? _switcher;
    private TrayIcon? _tray;
    // The tray's update entry, retitled to "Update now — vX" once a check has found one (RefreshUpdateMenuItem).
    private NativeMenuItem? _updateMenuItem;
    // Raises the Action Center notifications the update flow reports through, and hands back the click
    // on an "update available" one. Null if the platform has no notifier.
    private INotifier? _notifier;
    private OverlayStage? _stage;
    private SettingsWindow? _settingsWindow;
    private ISettingsStore? _settingsStore;
    private ISnapshotStore? _snapshots;
    private AppSettings _settings = new();
    private ChangelogWindow? _changelogWindow;
    // Changelog entries newer than the version that last ran here, collected at startup to pop once the
    // tray/HUD are up. Null when there's nothing to show (fresh install, same version, or feature off).
    private IReadOnlyList<ChangelogSection>? _pendingChangelog;
    private bool _shuttingDown; // set in Teardown so the settings-window Closed handler doesn't re-register hotkeys
    // The most recent update check, remembered so the command palette can offer "Update now" directly
    // (skipping a re-check) once a newer release has been found.
    private UpdateCheckResult? _lastUpdate;

    // "Last visited" = the desktop you came from, committed when a navigation completes (Ctrl+Alt
    // released) or on a discrete jump. Surfaced first in the jump palette so you can hop back.
    private DesktopId? _lastVisited;
    // The breadcrumb trail those same commits feed: one crumb per completed transaction, never the steps
    // in between. Ctrl+Alt+A / Ctrl+Alt+S walk it back and forward (StepHistory), Ctrl+Alt+Q flips
    // between its two newest entries (ToggleHistory); the map shows it top-right.
    private readonly NavHistory _history = new();
    private DesktopId? _gestureFrom; // where the in-progress keyboard gesture started
    private HotkeyModifiers _gestureMods; // the modifiers of the in-progress gesture's chord (watched for release)
    private DispatcherTimer? _gesturePoll;

    // Ambient notice that something outside Hypertree changed desktop, the published status file that
    // depends on it being true, and the pipe the CLI reaches us on. See Startup for how they connect.
    private PollingDesktopWatcher? _watcher;
    private StatusPublisher? _status;
    private ControlServer? _control;

    // Monitor-layout restore (the physical-screen axis, see docs/design/monitor-layout-restore.md): a
    // service that snapshots windows-per-monitor and puts them back across a dock cycle, driven by a poll
    // timer, plus the offer we hold between raising the redock notification and the user clicking it.
    private MonitorLayoutService? _layout;
    private DispatcherTimer? _layoutTimer;
    private Views.MonitorDebugWindow? _monitorDebugWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string[] args = Environment.GetCommandLineArgs();
            int shot = Array.IndexOf(args, "--shot");
            if (shot >= 0)
            {
                string dir = shot + 1 < args.Length ? args[shot + 1] : "captures";
                Dispatcher.UIThread.Post(() => { DesignShot.Capture(dir); desktop.Shutdown(); });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.Exit += (_, _) => Teardown();
            try { Startup(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Hypertree failed to start: {ex}");
                desktop.Shutdown(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Startup()
    {
        _desktops = PlatformServices.CreateDesktopController();
        _activator = PlatformServices.CreateForegroundActivator();
        _startup = PlatformServices.CreateStartupManager();
        _appCatalog = PlatformServices.CreateAppCatalog();
        _appLauncher = PlatformServices.CreateAppLauncher();
        _appIcons = PlatformServices.CreateAppIconProvider();
        RefreshAppsInBackground(); // warm the (slow) app-discovery cache off-thread, so the first Ctrl+Alt+O is instant
        _model = new NavigationModel(_desktops, new FileStateStore());
        // Desktops restored from persisted branches were created by Hypertree — track them so the
        // teardown guard still only ever destroys our own desktops.
        foreach (DesktopId id in _model.BranchDesktopIds()) _created.Add(id.Value);

        // The spatial map's own state (group colours + room positions), kept in its own file. Sparse — a
        // fresh install loads empty and everything falls back to defaults.
        _spatialStore = new FileSpatialStore();
        _spatial = _spatialStore.Load();

        var settingsStore = new FileSettingsStore();
        // First run = no settings file yet. Hypertree defaults to launching at login; we enable it and
        // write the settings file so this one-time default doesn't re-assert if the user later opts out.
        bool firstRun = !System.IO.File.Exists(settingsStore.Path);
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        _snapshots = new FileSnapshotStore();
        if (firstRun)
        {
            _startup.SetEnabled(true);
            _settingsStore.Save(_settings);
        }
        else if (_startup.IsEnabled)
        {
            // Self-heal a stale autostart path: an installed copy re-asserts its own ProcessPath here, so a
            // Run entry left pointing at an old install location — or at a build-tree copy that once wrote
            // itself in — is corrected the next time the real install runs. The write is installed-only and
            // idempotent (see StartupManager), so this is a no-op for a dev build and for an already-correct
            // entry.
            _startup.SetEnabled(true);
        }

        // First launch after a version bump: collect the changelog entries newer than the version that last
        // ran here, to pop once the tray/HUD are up (see the tail of Startup). A fresh install has no
        // LastSeenVersion, so it shows nothing — we just seed it. Stamp the current version either way.
        _pendingChangelog = ResolvePendingChangelog(_settings);
        if (_settings.LastSeenVersion != AppVersion)
        {
            _settings.LastSeenVersion = AppVersion;
            _settingsStore.Save(_settings);
        }

        _hud = new HudWindow(_mapCamera) { MapZoom = _settings.MapZoom }; // the flash draws the map at the same zoom

        // Persistent desktop-name pill over the taskbar. It re-reads the current desktop itself, but we
        // also poke it on every navigation so it never lags a keystroke.
        _taskbarLabel = new TaskbarLabel(CurrentDesktopLabel, _desktops);
        _model.Changed += () => _taskbarLabel?.Sync();
        ApplyTaskbarLabel();

        // The floating "click to switch" panel (borrowed from Perch). Off by default; it lists the stack
        // and jumps on click. It re-reads the stack on every navigation so its list and "here" marker stay
        // current, including switches made outside Hypertree (which route through the watcher → Changed).
        _switcher = new SwitcherWindow(
            () => _model!.BuildStatus(),
            JumpFromSwitcher,
            SaveSwitcherPosition,
            SaveSwitcherCollapsed,
            ExitFromSwitcher,
            _desktops, _settings.SwitcherCollapsed,
            _settings.SwitcherX, _settings.SwitcherY,
            _settings.SwitcherCollapsedX, _settings.SwitcherCollapsedY);
        _model.Changed += () => _switcher?.Sync();
        ApplySwitcher();

        StartWatchingDesktops();
        StartWatchingMonitors();
        StartPublishingStatus();
        StartControlServer();

        // One shared, persistent presentation surface for every overlay (map, palettes, prompts, move).
        // Swapping between them is an in-place content change, not a window teardown — no flash. Card
        // content floats over the live map, which the stage pulls from here.
        _stage = new OverlayStage(_desktops, _activator)
        {
            // The live card backdrop is the current spatial scene; read live, so no sync as it changes.
            SpatialProvider = () => (_model!.BuildSpatialSource(), _spatial),
            MapStyle = _settings.MapStyle, // board vs. metro, applied to every surface the stage draws
            MapZoom = _settings.MapZoom,   // the map zoom, applied to the card backdrops and the move flow's board
        };
        // Park the taskbar pill while the overlay is up (the map already shows where you are) — this also
        // removes the topmost-z fight that made the pill flash in/out when a dialog opened.
        _stage.Shown += () => { _taskbarLabel?.SetSuppressed(true); _switcher?.SetSuppressed(true); };
        _stage.Hidden += () => { _taskbarLabel?.SetSuppressed(false); _switcher?.SetSuppressed(false); };

        // The spatial map — the app's single map and "manage desktops" surface. Every edit is raised here and
        // serviced by App; DesktopId/Guid are resolved to the position-based model ops via Locate/IndexOfBranch.
        _spatialOverlay = new SpatialOverlay(_stage, _mapCamera, _settings.MapZoom, _settings.ShowMapLegend);
        _spatialOverlay.JumpRoomRequested += id => JumpFromMap(() => JumpToId(id));
        _spatialOverlay.ViewStyleToggleRequested += ToggleMapStyle; // v — cycle board ↔ metro ↔ ascii (app-wide)
        _spatialOverlay.SpatialStateChanged += () => _spatialStore?.Save(_spatial); // a move or recolour is written to spatial.json
        _spatialOverlay.ZoomChanged += zoom =>                      // +/− — persist the map zoom and mirror it to
        {                                                          // every other surface that draws the map (flash, backdrops, move flow)
            _settings.MapZoom = zoom;
            _settingsStore?.Save(_settings);
            if (_stage is not null) _stage.MapZoom = zoom;
            if (_hud is not null) _hud.MapZoom = zoom;
        };
        _spatialOverlay.LegendVisibilityChanged += show => { _settings.ShowMapLegend = show; _settingsStore?.Save(_settings); }; // l — persist the legend
        _spatialOverlay.SetRoomGroupRequested += OpenGroupPickerForRoom; // g — pick / create the room's group
        _spatialOverlay.DeleteRoomRequested += id =>       // Del — the confirm/teardown flow, resolved to a slot
        {
            if (_model?.Locate(id) is { } at)
                DeleteSelectedDesktop(new DesktopSelection(at.onMain, at.branchIndex, at.desktopIndex));
        };
        _spatialOverlay.DeleteGroupRequested += g =>       // Shift+Del — a group is a branch
        {
            int i = _model?.IndexOfBranch(g) ?? -1;
            if (i >= 0) ConfirmRemoveBranch(i);
        };
        _spatialOverlay.RenameRoomRequested += id =>       // r — rename the highlighted desktop
        {
            if (_model?.Locate(id) is { } at)
                RenameSelected(new DesktopSelection(at.onMain, at.branchIndex, at.desktopIndex));
        };
        _spatialOverlay.RenameGroupRequested += g =>       // Shift+R — rename the group (a branch)
        {
            int i = _model?.IndexOfBranch(g) ?? -1;
            if (i >= 0) RenameBranchOnMap(i);
        };
        _spatialOverlay.NewDesktopRequested += id =>       // n — new desktop in the highlighted room's group
        {
            if (_model?.Locate(id) is { } at)
                PromptNewDesktop(new DesktopSelection(at.onMain, at.branchIndex, at.desktopIndex));
        };
        _spatialOverlay.NewBranchRequested += () => OpenNewBranchDialog(null); // b — branch card over the map
        _spatialOverlay.MoveWindowsRequested += ToggleMoveWindows; // m — move this desktop's windows elsewhere
        _spatialOverlay.PullWindowsRequested += TogglePullWindows; // Shift+m — pull windows onto this desktop
        _spatialOverlay.FinderRequested += () => OpenSpotlight();  // f — finder over the map; Esc pops back
        _spatialOverlay.CommandPaletteRequested += () => ShowCommandPalette(overCurrent: true); // p — palette over the map
        _spatialOverlay.AppLauncherRequested += () => OpenAppLauncher(overCurrent: true); // o — launcher over the map

        _stage.Prewarm(); // size the overlay host now, so the first summon doesn't render at the top-left then jump

        RegisterHotkeys();
        BuildTray();
        BuildNotifier();

        // Launching Hypertree again (a re-clicked shortcut, the start-menu entry, a second login task) doesn't
        // start a rival copy — that launch signals us and exits. Answer the way a tray click does: open the
        // command palette, which both serves the intent and makes it obvious we were here all along.
        Program.Instance?.OnActivated(() => Dispatcher.UIThread.Post(OpenCommandPalette));

        // Pop the "what's new" window once everything else is up, so it lands over a fully-drawn tray rather
        // than racing startup. Background priority keeps it from delaying the first paint.
        if (_pendingChangelog is { Count: > 0 } changelog)
            Dispatcher.UIThread.Post(() => ShowChangelog(changelog), DispatcherPriority.Background);
    }

    // ── Outside-world surface: watcher → status file → control pipe ──────────────────────────────

    /// <summary>
    /// Start noticing desktop switches Hypertree didn't make (Win+Ctrl+Arrow, Task View, another launcher
    /// jumping to one of its windows).
    /// </summary>
    /// <remarks>
    /// Hypertree tracks a cursor, not the OS, and until now only re-anchored at the top of a navigation
    /// keystroke or when the map opened — so after an external switch the model, and the taskbar pill it
    /// feeds, stayed stale until Hypertree was next used. Publishing "where am I" to the CLI and to Perch
    /// makes that staleness visible to other apps, so it has to be fixed at the source rather than papered
    /// over per reader.
    /// </remarks>
    private void StartWatchingDesktops()
    {
        if (_desktops is null || _model is null) return;

        // Start from where we actually are. The model's constructor only looks for the current desktop on
        // the main timeline, so launching while inside a branch — the normal case after a restart, since
        // that's where you left off — leaves the cursor claiming main[0]. That was invisible while nothing
        // published it; the status file makes it wrong out loud, and the taskbar pill was wrong all along.
        _model.AnchorToCurrent();

        _watcher = new PollingDesktopWatcher(_desktops);
        // Re-anchor the cursor onto wherever we actually are. That raises Changed, which syncs the pill and
        // schedules a status write — so every reader converges on the truth from this one hook.
        _watcher.CurrentChanged += _ => _model!.AnchorToCurrent();
        // Our own navigation must not come back round as an "external" change on the next tick.
        _model.Changed += () =>
        {
            if (_desktops is not null) _watcher?.Acknowledge(_desktops.Current);
        };
        _watcher.Start();
    }

    private void StartPublishingStatus()
    {
        if (_model is null) return;
        _status = new StatusPublisher(_model, AppVersion, ResolveCliPath());
        _model.Changed += () => _status?.Schedule();
        _status.PublishNow(); // be readable the instant the tray is up, not one navigation later
    }

    private void StartControlServer()
    {
        _control = new ControlServer(HandleControlRequest);
        _control.Start();
    }

    // ── Monitor-layout restore: snapshot windows-per-monitor on undock, offer to put them back on redock ──

    /// <summary>
    /// Start tracking the physical monitor topology. A background-priority poll (mirroring
    /// <see cref="PollingDesktopWatcher"/> — dock/undock is not latency-sensitive and the read is cheap)
    /// drives <see cref="MonitorLayoutService.Tick"/>; the service saves the arrangement on undock and
    /// raises <see cref="MonitorLayoutService.RestoreAvailable"/> on redock, which we surface as a
    /// clickable notification. Best-effort: if the platform can't provide the controller, layout tracking
    /// simply stays off rather than failing startup.
    /// </summary>
    private void StartWatchingMonitors()
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

        _notifier?.Show(
            "Restore your window layout?",
            $"You're back on {snapshot.Monitors.Count} monitors. Click to move {toMove} window{(toMove == 1 ? "" : "s")} back into place.",
            action: RestoreLayoutAction, replaces: MonitorLayoutNoticeKey);
    });

    private void DoRestoreMonitorLayout(MonitorLayoutSnapshot snapshot)
    {
        if (_layout is null) return;

        // Only curtain the restore when the maximize-cross-monitor trick is in play — that's the move that
        // visibly pops; a plain move needs no cover. Report through the same silent confirmation either way.
        if (_layout.PlanRestore(snapshot).NeedsCurtain)
            Views.RestoreCurtain.Run(_layout.CurrentMonitors(), "Restoring positions…", () => ApplyRestore(snapshot));
        else
            ApplyRestore(snapshot);
    }

    private void ApplyRestore(MonitorLayoutSnapshot snapshot)
    {
        if (_layout is null) return;
        RestoreReport r = _layout.Restore(snapshot);
        int skipped = r.Gone + r.MonitorMissing + r.Refused;
        _notifier?.Show(
            "Layout restored",
            $"{r.Placed} window{(r.Placed == 1 ? "" : "s")} put back"
                + (skipped > 0 ? $" · {skipped} skipped ({r.Gone} closed, {r.MonitorMissing} off-screen, {r.Refused} refused)" : "."),
            silent: true, replaces: MonitorLayoutNoticeKey);
    }

    // The debug overlay: a scrollable list of virtual desktops, each with a box per monitor listing the
    // windows on it, every window showing the monitor it's on and the one the saved layout wants. Combines
    // the two axes — desktop grouping from IDesktopController, current/wanted monitor from the layout
    // snapshot vs the saved capture for the monitors present now. Rough by intent; it's a debug aid.
    private void OpenMonitorDebugOverlay()
    {
        if (_layout is null || _desktops is null || _activator is null) return;

        // Offer restore when there's a saved layout for the set we're on. The action re-resolves the layout
        // at click time and does not close the window — it re-polls so you can watch where windows land.
        bool hasSaved = _layout.AutoFor(_layout.CaptureNow().SetKey) is not null;
        Action? onRestore = hasSaved ? RestoreCurrentSetLayout : null;
        Action? onTrace = hasSaved ? TraceRestoreToFile : null;

        _monitorDebugWindow?.Close();
        _monitorDebugWindow = new Views.MonitorDebugWindow(BuildMonitorDebugView, _activator, onRestore, onTrace);
        _monitorDebugWindow.Closed += (_, _) => _monitorDebugWindow = null;
        _monitorDebugWindow.Show();
        _monitorDebugWindow.TakeFocus();
    }

    // Rebuild the overlay's view data from the live state — called on open and on every refresh/restore.
    private (IReadOnlyList<Views.DebugDesktopRow> desktops, string subtitle) BuildMonitorDebugView()
    {
        if (_layout is null || _desktops is null)
            return (Array.Empty<Views.DebugDesktopRow>(), "unavailable");

        MonitorLayoutSnapshot snap = _layout.CaptureNow();
        MonitorLayoutSnapshot? reference = _layout.AutoFor(snap.SetKey);

        Dictionary<long, WindowPlacement> curByHwnd = snap.Windows.ToDictionary(w => w.Hwnd);
        Dictionary<long, WindowPlacement> wantByHwnd =
            reference?.Windows.ToDictionary(w => w.Hwnd) ?? new Dictionary<long, WindowPlacement>();
        var monName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorRef m in snap.Monitors) monName[m.StableId] = m.Friendly;
        string Name(string id) => id.Length > 0 && monName.TryGetValue(id, out string? f) && f.Length > 0 ? f : (id.Length > 0 ? id : "?");

        var desktops = new List<Views.DebugDesktopRow>();
        foreach (DesktopInfo d in _desktops.List())
        {
            var byMon = new Dictionary<string, List<Views.DebugWindowRow>>();
            foreach (WindowInfo w in _desktops.WindowsOn(d.Id))
            {
                long hwnd = (long)w.Hwnd;
                string curId = curByHwnd.TryGetValue(hwnd, out WindowPlacement? cp) ? cp.MonitorStableId : "";
                string? wantId = wantByHwnd.TryGetValue(hwnd, out WindowPlacement? wp) ? wp.MonitorStableId : null;
                string boxKey = curId.Length > 0 ? curId : "?";
                if (!byMon.TryGetValue(boxKey, out var list)) byMon[boxKey] = list = new List<Views.DebugWindowRow>();
                list.Add(new Views.DebugWindowRow(w.Title, Name(curId), wantId is null ? null : Name(wantId)));
            }

            var boxes = new List<Views.DebugMonitorBox>();
            foreach (MonitorRef m in snap.Monitors) // monitors in a stable order; empty ones shown too
                boxes.Add(new Views.DebugMonitorBox(Name(m.StableId),
                    byMon.TryGetValue(m.StableId, out var list) ? list : new List<Views.DebugWindowRow>()));
            if (byMon.TryGetValue("?", out var unknown)) // windows we couldn't attribute to a present monitor
                boxes.Add(new Views.DebugMonitorBox("(unknown)", unknown));

            string label = string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name;
            desktops.Add(new Views.DebugDesktopRow(label, boxes));
        }

        string subtitle = reference is null
            ? $"set {snap.SetKey} · {snap.Monitors.Count} monitors · no saved layout for this set — current placement only"
            : $"set {snap.SetKey} · {snap.Monitors.Count} monitors · vs saved layout ({reference.Windows.Count} windows)";
        return (desktops, subtitle);
    }

    // Restore the saved layout for whatever monitor set we're on now (re-resolved at call time, so the debug
    // overlay's Restore button always acts on the current set).
    private void RestoreCurrentSetLayout()
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
            _notifier?.Show("Restore trace", "No saved layout for these monitors to trace.", silent: true,
                replaces: MonitorLayoutNoticeKey);
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
                _notifier?.Show("Restore trace", $"Couldn't open the report ({ex.Message}). It's at {path}.",
                    silent: true, replaces: MonitorLayoutNoticeKey);
            }

            _monitorDebugWindow?.Focus();
        }, TimeSpan.FromMilliseconds(800));
    }

    /// <summary>
    /// Where <c>htree.exe</c> sits, if it shipped alongside us. Published in the status file so a reader
    /// never has to guess at an install layout to find the CLI — and so a dev build running from source
    /// advertises the CLI from that same build rather than an installed one.
    /// </summary>
    private static string? ResolveCliPath()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(dir)) return null;
            string cli = System.IO.Path.Combine(dir, "htree.exe");
            return System.IO.File.Exists(cli) ? cli : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Carry out a request that arrived on the control pipe. Always runs on the UI thread (the server
    /// marshals it there), because everything it touches — the desktop COM, the navigation model — belongs
    /// to that thread.
    /// </summary>
    private ControlResponse HandleControlRequest(ControlRequest request) => request.Command switch
    {
        ControlRequest.CommandPing => ControlResponse.Success(),
        ControlRequest.CommandGoto => HandleGoto(request.Goto),
        _ => ControlResponse.Failure(ExitCode.BadUsage, $"Unknown command '{request.Command}'."),
    };

    private ControlResponse HandleGoto(GotoRequest? go)
    {
        if (_model is null || _desktops is null)
            return ControlResponse.Failure(ExitCode.Failed, "Hypertree is still starting up.");
        if (go is null)
            return ControlResponse.Failure(ExitCode.BadUsage, "goto needs a target.");

        // The client resolved its target against a status file that may be a moment old, and desktops can
        // disappear from Task View at any time. Reconcile so we act on the live layout, exactly as the map
        // and the jump palette do before offering a jump.
        _model.Reconcile();

        DesktopId from = _desktops.Current;
        GoToResult result = _model.GoTo(go.BranchId, go.Desktop, out string landed);
        if (result == GoToResult.NoSuchBranch)
            return ControlResponse.Failure(ExitCode.UnknownTarget, "That branch no longer exists.");
        if (result == GoToResult.NoSuchDesktop)
            return ControlResponse.Failure(ExitCode.UnknownTarget, "That desktop no longer exists.");

        // Same bookkeeping a jump from the map does, so "last visited" and the pill stay honest about a
        // move that came from outside.
        RecordVisit(from);
        _status?.PublishNow(); // the caller may read the file immediately; don't make it wait out the debounce
        return ControlResponse.Success(landed);
    }

    // Picks the changelog sections to surface on this launch: nothing unless the feature is on, we have a
    // recorded last-seen version, and it differs from the running one. Fresh installs (null last-seen) and
    // same-version launches show nothing.
    private static IReadOnlyList<ChangelogSection>? ResolvePendingChangelog(AppSettings settings)
    {
        if (!settings.ShowChangelogOnUpdate) return null;
        if (string.IsNullOrWhiteSpace(settings.LastSeenVersion)) return null; // fresh install — nothing to diff
        if (settings.LastSeenVersion == AppVersion) return null;              // same version — no update
        var markdown = ChangelogMarkdown.LoadEmbedded();
        if (markdown is null) return null;
        var sections = ChangelogParser.UnseenSince(markdown, settings.LastSeenVersion, AppVersion);
        return sections.Count > 0 ? sections : null;
    }

    private void ShowChangelog(IReadOnlyList<ChangelogSection> sections)
    {
        if (_activator is null) return;
        string subhead = sections.Count == 1
            ? "Here's what changed in this update."
            : $"Here's what changed across the last {sections.Count} releases.";

        _changelogWindow?.Close();
        _changelogWindow = new ChangelogWindow("What's new in Hypertree", subhead, sections, _activator,
            // "Don't show changelogs again" flips the feature off for good.
            onSuppress: () => { _settings.ShowChangelogOnUpdate = false; _settingsStore?.Save(_settings); })
        {
            Topmost = true, // sit above the map/flash if one is showing
        };
        _changelogWindow.Closed += (_, _) => _changelogWindow = null;
        _changelogWindow.Show();
        _changelogWindow.TakeFocus();
    }

    // Register every command's current chord (defaults overlaid with the user's rebindings). Called at
    // startup and again when the settings window closes (resuming after SuspendHotkeys), picking up rebinds.
    private void RegisterHotkeys()
    {
        foreach ((HotkeyCommand cmd, HotkeyChord chord) in _settings.ResolveHotkeys())
        {
            var hk = PlatformServices.CreateGlobalHotkey();
            Action onPressed = ActionFor(cmd, chord.Modifiers);
            if (hk.Register(chord.Modifiers, chord.Key, () => Dispatcher.UIThread.Post(onPressed))) _hotkeys.Add(hk);
            else { hk.Dispose(); Console.Error.WriteLine($"Hotkey {chord.Display()} ({cmd}) was refused by the OS."); }
        }
    }

    // What each command does when its chord fires. Navigation carries the chord's modifiers so the flash
    // knows which keys to watch for the hold-to-keep release (they're rebindable, not always Ctrl+Alt).
    private Action ActionFor(HotkeyCommand cmd, HotkeyModifiers mods) => cmd switch
    {
        HotkeyCommand.CommandPalette => ToggleCommandPalette,
        HotkeyCommand.OpenMap        => ToggleMap,
        HotkeyCommand.AppLauncher    => ToggleAppLauncher,
        HotkeyCommand.ToggleSwitcher => ToggleSwitcherCollapsed,
        HotkeyCommand.MoveWindows    => ToggleMoveWindows, // no default chord; only a user's kept rebinding fires this
        HotkeyCommand.Peek           => () => Peek(mods),
        HotkeyCommand.UndoNav        => () => StepHistory(back: true, mods),
        HotkeyCommand.RedoNav        => () => StepHistory(back: false, mods),
        HotkeyCommand.ToggleNav      => () => ToggleHistory(mods),
        _ when NavCommands.TryGetValue(cmd, out NavAction action) => () => Navigate(action, mods),
        _ => () => { },
    };

    // Unregister every global hotkey (each IGlobalHotkey owns a message-loop thread; Dispose posts WM_QUIT
    // to unwind it). Used to suspend hotkeys while the settings window is open; RegisterHotkeys resumes them.
    private void SuspendHotkeys()
    {
        foreach (IGlobalHotkey hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
    }

    // Navigate. While the map overlay is open it stays open (its windows are pinned across the
    // desktop switch) and re-homes its selection onto the desktop we land on; otherwise the flash shows.
    // <paramref name="mods"/> is the chord's modifier layer — the flash holds while these are down.
    // With "show before moving" on (the default), the press that raises the flash doesn't also move —
    // see <see cref="RevealOnly"/>.
    private void Navigate(NavAction action, HotkeyModifiers mods)
    {
        if (_model is null || _desktops is null) return;
        // The move flow owns the arrows while it's up (its own plain-arrow handlers drive it), so an
        // out-of-habit nav chord mustn't also navigate underneath.
        if (_stage?.Current is MoveContent) return;
        // Something outside Hypertree may have moved us since our last navigation (another launcher
        // jumping to a window, Task View, Win+Ctrl+Arrow). Start this move from where we actually are,
        // not from where our own cursor was left. Mid-gesture it's a no-op — we're already standing on
        // the desktop the previous keystroke switched to.
        _model.AnchorToCurrent();
        // Start of a gesture: remember where we came from (and which modifiers to watch), so releasing
        // them can record it as "last visited". A poll watches for the release (flashing or in the map).
        _gestureFrom ??= _desktops.Current;
        _gestureMods = mods;

        bool softMotion = WindowFx.SystemAnimationsEnabled();
        bool animate = _settings.AnimateNavigation && softMotion;
        bool inMap = AnyMapOpen();

        // The direction resolves to the nearest room; "crossing a group" is the analog of a dive/surface —
        // it's what the show-before-moving reveal keys off.
        (int dx, int dy) = DirectionOf(action);
        SpatialScene pre = SpatialScene.From(_model.BuildSpatialSource(), _spatial);
        DesktopId? target = SpatialNavigation.NextInDirection(pre, _desktops.Current, dx, dy);
        bool crossing = target is { } t &&
                        SpatialNavigation.GroupOf(pre, t) != SpatialNavigation.GroupOf(pre, _desktops.Current);

        // A cold press with "show before moving" on only raises the board — it doesn't move (a plain fade,
        // no wipe). It applies to a move out of the current group; a move that stays in it goes straight away,
        // and the map never needs the reveal.
        bool revealOnly = _settings.DisplayBeforeMoving && crossing && !inMap && _hud is { IsVisible: false };

        // Cover the screen BEFORE switching, so the switch happens behind the dim rather than flashing the lit
        // destination desktop for a beat. The map is its own always-up surface, and a reveal doesn't switch.
        if (!inMap && !revealOnly) _hud?.Cover();

        // Apply the move. It reports whether the desktop actually changed (false at an edge / already there),
        // so a move that goes nowhere doesn't play a directional wipe.
        bool moved = !revealOnly && target is { } tid && JumpToId(tid);

        // In the map, the nav chord switches for real and the selection follows; otherwise the flash shows,
        // with the green marker on the gesture origin so direction/distance reads at a glance.
        if (inMap) SyncOpenMapToCurrent();
        else FlashBoard(_gestureFrom, mods, moved ? action : null, animate && moved, softMotion);
        StartGesturePoll();
    }

    private static (int X, int Y) DirectionOf(NavAction action) => action switch
    {
        NavAction.MoveLeft => (-1, 0),
        NavAction.MoveRight => (1, 0),
        NavAction.Surface => (0, -1),
        NavAction.Dive => (0, 1),
        _ => (0, 0),
    };

    // Raise the transient flash on the current spatial arrangement. <paramref name="cameFrom"/> marks the
    // gesture origin with the green "here" outline (null for a plain result/peek flash).
    private void FlashBoard(DesktopId? cameFrom, HotkeyModifiers mods, NavAction? move, bool animate, bool fade)
    {
        if (_hud is null || _model is null) return;
        SpatialScene scene = SpatialScene.From(_model.BuildSpatialSource(cameFrom), _spatial);
        _hud.Flash(scene, _settings.MapStyle, mods, move, animate, _settings.SweepFromLeadingEdge, fade);
    }

    // Peek: raise the flash on where we actually are and hold it while <paramref name="mods"/> stay down,
    // without moving. A preview on demand — and, since the board is up afterwards, a following nav chord
    // moves for real (the same hand-off as "show before moving", but triggered explicitly and regardless of
    // that setting). No gesture is recorded: nothing moved, so there's no "last visited" to remember.
    private void Peek(HotkeyModifiers mods)
    {
        if (_model is null || _desktops is null) return;
        // The move flow owns the arrows while it's up; the map is already a persistent board — neither wants
        // a transient peek over it.
        if (_stage?.Current is MoveContent) return;
        if (AnyMapOpen()) return;
        _model.AnchorToCurrent(); // show where we stand now, not our stale cursor
        // A peek has no direction, so there's nothing to wipe — it's pure appearance, and fades up.
        FlashBoard(null, mods, move: null, animate: false, fade: WindowFx.SystemAnimationsEnabled());
    }

    // A double-click / arrow-driven jump from the map: switch to the chosen desktop, record where we
    // came from, then re-home the selection onto it (green + blue rejoin), keeping the map open.
    private void JumpFromMap(Func<bool> doJump)
    {
        if (_desktops is null || _model is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        RecordVisit(from);
        SyncOpenMapToCurrent();
    }

    private void StartGesturePoll()
    {
        if (_gesturePoll is null)
        {
            _gesturePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _gesturePoll.Tick += (_, _) => { if (!ModifierKeys.ModifiersHeld(_gestureMods)) CompleteGesture(); };
        }
        if (!_gesturePoll.IsEnabled) _gesturePoll.Start();
    }

    // The gesture is over once Ctrl+Alt is released: if we actually moved, the desktop we started on
    // becomes "last visited" and the whole gesture lands on the trail as one crumb — the transaction's
    // end point, not every desktop it stepped through.
    private void CompleteGesture()
    {
        _gesturePoll?.Stop();
        if (_gestureFrom is { } from) RecordVisit(from);
        _gestureFrom = null;
    }

    // ── Navigation history (breadcrumb trail · Ctrl+Alt+A back / S forward / Q flip) ──

    // One navigation transaction ended: we set out from `from` and settled wherever the OS now says.
    // A no-op when we ended up back where we started. This is the ONLY place the trail is written, so
    // every crumb is a transaction's end point — intermediate steps and undo/redo moves never land on it.
    private void RecordVisit(DesktopId from)
    {
        if (_desktops is null || _desktops.Current == from) return;
        _lastVisited = from;
        _history.Record(from, _desktops.Current);
        RefreshOverlay(); // the map's history panel shows the new crumb without waiting for the next render
    }

    // Walk the trail: back to the previous transaction's end point, or forward again. The trail itself is
    // not rewritten — only a real navigation does that (truncating the forward tail; see NavHistory.Record).
    // <paramref name="mods"/> is the chord's modifier layer, so the flash holds while it's held, exactly
    // like a navigation keystroke.
    private void StepHistory(bool back, HotkeyModifiers mods)
    {
        if (PrepareHistoryJump() is not { } cur) return;

        DesktopId? target;
        if (back && _history.Current is { } tip && tip != cur)
        {
            // We've wandered off the trail (an external switch, or a gesture still in flight) — "back"
            // first returns to where the trail stands, without consuming an undo step.
            target = tip;
        }
        else
        {
            target = back ? _history.Undo() : _history.Redo();
            // Pruning can leave the neighbouring entry equal to where we already are — step past it.
            while (target is { } same && same == cur) target = back ? _history.Undo() : _history.Redo();
        }
        JumpAlongTrail(target, cur, mods);
    }

    // Ctrl+Alt+Q — bounce between the trail's two newest entries (the alt-tab of desktops): press to hop
    // to the other one, press again to hop back, for ever. NavHistory.Toggle picks the target and parks
    // the cursor on it, so the map's panel follows and a real navigation branches from where the hop left you.
    private void ToggleHistory(HotkeyModifiers mods)
    {
        if (PrepareHistoryJump() is not { } cur) return;
        JumpAlongTrail(_history.Toggle(cur), cur, mods);
    }

    // Shared guard + freshen for every history jump. Work against the live layout: drop desktops deleted
    // behind our back from both the model and the trail. Returns where we stand, or null when a history
    // jump can't run right now (still starting up, or the move flow owns navigation).
    private DesktopId? PrepareHistoryJump()
    {
        if (_model is null || _desktops is null) return null;
        if (_stage?.Current is MoveContent) return null;
        _model.Reconcile();
        _history.Prune(id => _model.Locate(id) is not null);
        return _desktops.Current;
    }

    // Switch to a desktop the trail picked, presenting it like a navigation: the open map follows the
    // switch; otherwise the board flashes with the origin marked green. No wipe — a history jump has no
    // row/column direction to carry. A null / untracked target is a quiet no-op.
    private void JumpAlongTrail(DesktopId? target, DesktopId cur, HotkeyModifiers mods)
    {
        if (_model is null || target is not { } id || _model.Locate(id) is not { } at) return;

        if (at.onMain) _model.GoToTop(at.desktopIndex);
        else _model.GoToBranchDesktop(at.branchIndex, at.desktopIndex);

        if (AnyMapOpen()) SyncOpenMapToCurrent();
        else FlashBoard(cur, mods, move: null, animate: false, fade: WindowFx.SystemAnimationsEnabled());
    }

    // A discrete jump from the spotlight palette: switch, record where we came from, then close the overlay
    // outright — a jump physically moves you, so it's terminal (you don't return to the map behind it).
    private void Jump(Func<bool> doJump)
    {
        if (_desktops is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        RecordVisit(from);
        _stage?.Dismiss();
    }

    private void ToggleMap()
    {
        if (_model is null || _spatialOverlay is null || _desktops is null) return;
        if (_spatialOverlay.IsOpen) { _spatialOverlay.Close(); return; }

        _model.Reconcile(); // drop any externally-deleted desktops before showing the map
        OpenMap();
    }

    // Open the spatial map. Reframe first, since its metrics differ from the flash offset the shared camera
    // may hold.
    private void OpenMap()
    {
        if (_model is null) return;
        _mapCamera.Reframe();
        SpatialSource source = _model.BuildSpatialSource();
        PruneSpatial(source); // forget positions for desktops that no longer exist, so spatial.json doesn't grow
        _spatialOverlay?.Open(source, _spatial);
    }

    // Drop stored positions for desktops the source no longer contains (deleted since they were placed).
    // GUIDs are unique so a stale entry would never wrongly match, but this keeps the file from growing.
    private void PruneSpatial(SpatialSource source)
    {
        if (_spatialStore is null) return;
        var live = source.Groups.SelectMany(g => g.Desktops).Select(d => d.Id.Value.ToString()).ToHashSet();
        int removed = _spatial.Positions.Keys.Where(k => !live.Contains(k)).ToList()
            .Count(k => _spatial.Positions.Remove(k));
        if (removed > 0) _spatialStore.Save(_spatial);
    }

    // Resolve a spatial jump: turn a desktop id into the row/branch position the model jumps by.
    private bool JumpToId(DesktopId id)
    {
        if (_model is null) return false;
        if (_model.Locate(id) is not { } at) return false;
        return at.onMain ? _model.GoToTop(at.desktopIndex)
                         : _model.GoToBranchDesktop(at.branchIndex, at.desktopIndex);
    }

    private bool AnyMapOpen() => _spatialOverlay is { IsOpen: true };

    // Re-home the open map onto the desktop we're now on — after a real switch.
    private void SyncOpenMapToCurrent()
    {
        if (_model is null) return;
        if (_spatialOverlay is { IsOpen: true }) _spatialOverlay.SyncToCurrent(_model.BuildSpatialSource(), _spatial);
    }

    // Prime the map with a fresh board: redraws now if it's the current surface, else stashes it so the
    // map shows the update the next time the stage unwinds back to it (after an action completes on a card).
    // SetSource itself decides render-now vs stash, so this must NOT be gated on IsOpen — an action run from
    // a card on top of the map (e.g. the group picker) leaves the map not-current, and gating here would drop
    // the update so the map re-presents stale.
    private void RefreshOverlay()
    {
        if (_model is null) return;
        _spatialOverlay?.SetSource(_model.BuildSpatialSource(), _spatial);
    }

    // ── Set a room's group (g on the spatial map) ───────────────────────────────────

    // g on the spatial map: pick the group (branch) the highlighted room belongs to, or type a new name to
    // create one. Reuses the shared palette — its "create «name»" row is exactly the "create xxxx" affordance
    // — over the durable spatial map, so choosing reassigns the desktop and unwinds back to the recoloured
    // map. The room's current group is left out of the list (you can't move it to where it already is).
    private void OpenGroupPickerForRoom(DesktopId id)
    {
        if (_model is null || _stage is null) return;
        SpatialSource source = _model.BuildSpatialSource();
        SpatialGroupSource? owner = source.Groups.FirstOrDefault(g => g.Desktops.Any(d => d.Id == id));
        if (owner is null) return; // the room vanished (e.g. an external delete) — nothing to regroup

        string room = owner.Desktops.First(d => d.Id == id).Label;

        var items = new List<PaletteItem>();
        foreach (SpatialGroupSource g in source.Groups)
        {
            if (g.Id == owner.Id) continue;                  // skip the group it's already in
            Guid target = g.Id;
            int n = g.Desktops.Count;
            items.Add(new PaletteItem(
                g.IsMain ? "main" : g.Name,
                n == 1 ? "1 room" : $"{n} rooms",
                g.IsMain ? "○" : "●",
                () => AssignRoomToGroup(id, target)));
        }

        // A typed name that matches no existing group offers to create it, seeded with the moved room.
        PaletteItem? CreateRow(string q) =>
            new($"Create “{q}”", "new group", "＋", () => AssignRoomToNewGroup(id, q));

        _stage.Present(new PaletteContent(
            $"Set group for “{room}”…",
            "↑↓ move · ↵ choose · type a name to create · Esc back",
            items, CreateRow));
    }

    private void AssignRoomToGroup(DesktopId id, Guid groupId)
    {
        if (_model is null) return;
        _model.MoveDesktopToGroup(id, groupId);
        RefreshOverlay(); // stash the regrouped board; the stage re-presents the map as the palette completes
    }

    private void AssignRoomToNewGroup(DesktopId id, string name)
    {
        if (_model is null) return;
        name = name.Trim();
        if (name.Length == 0) return;
        _model.MoveDesktopToNewBranch(id, name);
        RefreshOverlay();
    }

    // ── Move windows to another desktop (m on the map) ──────────────────────────────

    // Phase 1: snapshot the current desktop's windows and open the picker. Re-press toggles it closed.
    private void ToggleMoveWindows()
    {
        if (_model is null || _desktops is null || _stage is null) return;
        if (_stage.Current is MoveContent) { _stage.Back(); return; } // re-press cancels (via OnRemoved)

        _model.Reconcile();
        _moveOrigin = _desktops.Current;
        var session = new WindowMoveSession(_desktops.WindowsOn(_moveOrigin.Value));

        // Presenting the move content on the stage swaps out any open map/palette in place. Navigation
        // and the move are serviced here; the board is pulled live from the model, centred on the origin.
        var content = new MoveContent(session, _settings.PickerZoom) { BoardProvider = () => _model!.BuildMap(_moveOrigin) };
        content.NavigateRequested += a => _model!.Apply(a);
        content.MoveRequested += MoveSelectedWindows;
        content.Cancelled += CancelMove;
        content.ZoomChanged += PersistPickerZoom; // Ctrl+/Ctrl− — remember the thumbnail size
        // Launched from the map → push over it so completing/cancelling unwinds back to the map (its durable
        // base). Otherwise (hotkey / tray, no map open) it's a fresh root that dismisses to the desktop —
        // Back/CompleteToBase then behave exactly like the old Dismiss.
        if (_spatialOverlay?.IsOpen == true) _stage.Present(content);
        else _stage.Summon(content);
    }

    // Phase 2 commit: we've navigated to the destination (it's the current desktop), so move each
    // selected window there. The content dismisses the stage itself; we stay on the destination.
    private void MoveSelectedWindows(IReadOnlyList<nint> hwnds)
    {
        if (_desktops is null) return;
        DesktopId dest = _desktops.Current;
        foreach (nint h in hwnds)
        {
            try { _desktops.MoveWindowToDesktop(h, dest); } catch { /* window may have closed — best-effort */ }
        }
        _moveOrigin = null;
        RefreshOrFlash(); // flash the destination with its now-updated window counts
    }

    // Cancel (Esc / Backspace / click-away / re-press) — raised from the move content's OnRemoved as the
    // stage dismisses it: return to where the move started (phase 2 may have navigated us away) and
    // re-anchor the model there.
    private void CancelMove()
    {
        if (_desktops is not null && _moveOrigin is { } origin && _desktops.Current != origin)
            _desktops.SwitchTo(origin);
        _moveOrigin = null;
        _model?.Resync();
        RefreshOverlay(); // if we opened over the map, it's about to be re-presented — give it a fresh board
    }

    // ── Pull windows from other desktops onto this one (Shift+m on the map) ─────────

    // Snapshot every window on the other desktops and open the picker. Re-press toggles it closed. Unlike
    // move there's no second phase and no origin to restore: the destination is always where we already are.
    private void TogglePullWindows()
    {
        if (_model is null || _desktops is null || _stage is null) return;
        if (_stage.Current is PullContent) { _stage.Back(); return; } // re-press cancels

        _model.Reconcile();
        var session = new WindowMoveSession(_desktops.WindowsElsewhere());
        var content = new PullContent(session, _settings.PickerZoom);
        content.PullRequested += PullSelectedWindows;
        content.ZoomChanged += PersistPickerZoom; // Ctrl+/Ctrl− — remember the thumbnail size
        // Launched from the map → push over it so completing/cancelling unwinds back to the map. Otherwise
        // (hotkey / tray, no map open) it's a fresh root that dismisses to the desktop.
        if (_spatialOverlay?.IsOpen == true) _stage.Present(content);
        else _stage.Summon(content);
    }

    // Commit: move each selected window onto the current desktop (where we already are). The content
    // dismisses the stage itself; we stay put and flash the now-updated window counts.
    private void PullSelectedWindows(IReadOnlyList<nint> hwnds)
    {
        if (_desktops is null) return;
        DesktopId here = _desktops.Current;
        foreach (nint h in hwnds)
        {
            try { _desktops.MoveWindowToDesktop(h, here); } catch { /* window may have closed — best-effort */ }
        }
        RefreshOrFlash();
    }

    // Both picker flows share one persisted thumbnail size (settings.PickerZoom), so a zoom set in "move"
    // carries over to "pull" and survives a restart — mirrors how the map's zoom is remembered.
    private void PersistPickerZoom(double zoom)
    {
        _settings.PickerZoom = zoom;
        _settingsStore?.Save(_settings);
    }

    // ── Manage-map actions (r / Del / Shift+Del / n) ───────────────────────────────

    // r on the map: open the rename prompt prefilled with the selected desktop's current name. On confirm,
    // rename the OS desktop and the model's stored label, then refresh the map in place. The prompt steals
    // focus (it's a top-most window); when it closes we hand the stage its key focus back so the arrow
    // selection resumes.
    private void RenameSelected(DesktopSelection sel)
    {
        if (_model is null || _desktops is null) return;

        var peek = sel.OnMain
            ? _model.PeekTopDesktop(sel.DesktopIndex)
            : _model.PeekBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
        if (peek is null) return;

        // A card over the map, prefilled + select-all so the first keystroke replaces the name. On confirm
        // the model is relabelled and the map primed; CompleteToBase then unwinds to the map, now relabelled.
        _stage?.Present(new PromptContent("Rename desktop",
            "Type a new name for this desktop.", "desktop name",
            name =>
            {
                try { _desktops.Rename(peek.Value.id, name); } catch { /* best-effort — desktop may have gone */ }
                _model.SetDesktopLabel(sel.OnMain, sel.BranchIndex, sel.DesktopIndex, name);
                RefreshOverlay();
            },
            confirmLabel: "Rename", prefill: peek.Value.label, selectAll: true));
    }

    // Shift+R on the map: rename the branch at `index` (main has no branch, so the overlay never raises this
    // for it). A card over the map, prefilled + select-all; on confirm the model relabels, persists and the
    // map redraws with the new branch name.
    private void RenameBranchOnMap(int index)
    {
        if (_model is null) return;
        if (_model.BranchNameAt(index) is not { } current) return;

        _stage?.Present(new PromptContent("Rename branch",
            "Type a new name for this branch.", "branch name",
            name =>
            {
                _model.RenameBranch(index, name);
                RefreshOverlay();
            },
            confirmLabel: "Rename", prefill: current, selectAll: true));
    }

    // Del on the map: delete the selected desktop (with a confirm), resolving main vs. branch.
    private void DeleteSelectedDesktop(DesktopSelection sel)
    {
        if (sel.OnMain) DeleteTopDesktop(sel.DesktopIndex);
        else DeleteBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
    }

    // Shift+Del on the map: delete an entire branch (all its desktops) behind a confirm.
    private void ConfirmRemoveBranch(int index)
    {
        if (_model is null) return;
        var map = _model.BuildMap();
        if (index < 0 || index >= map.Branches.Count) return;
        NavMapBranch g = map.Branches[index];
        Confirm($"Delete branch “{g.Name}”?\nIts {g.Desktops.Count} desktop{(g.Desktops.Count == 1 ? "" : "s")} " +
                "are removed and any windows on them move to another desktop.", () => RemoveBranch(index));
    }

    // n on the map: prompt for a name, create a new desktop at the end of the selected row (no switch — the
    // manage surface stays where you are), then home the selection onto it so you can rename/act on it
    // immediately. The row is the point: pressing n inside a branch grows *that* branch, which is where you
    // were already looking, rather than dropping the desktop onto main for you to drag back up.
    private void PromptNewDesktop(DesktopSelection sel)
    {
        if (_model is null || _desktops is null) return;

        // Resolved before the prompt opens, so the card can say where the desktop will land.
        string? branch = sel.OnMain ? null : _model.BranchNameAt(sel.BranchIndex);

        _stage?.Present(new PromptContent("New desktop",
            $"Create a new desktop on {(branch is null ? "the main timeline" : $"branch “{branch}”")}. " +
            "You stay on the current desktop.",
            "desktop name (e.g. email)",
            name =>
            {
                if (branch is null)
                {
                    DesktopId mainId = _desktops.Create(name); // a main-timeline desktop is the user's own — not tracked in _created
                    _model.SyncTopRow();    // picks up the new desktop (appended to the top row)
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(mainId); // home the cursor to the just-created room
                    return;
                }

                // In a branch: the OS name carries the branch prefix (as branch creation does) while the tile
                // keeps the bare label, and it's tracked in _created so teardown can clean it up with the rest
                // of the branch.
                DesktopId id = _desktops.Create($"{branch} · {name}");
                _created.Add(id.Value);
                if (_model.AddDesktopToBranch(sel.BranchIndex, new DesktopRef(id, name)) is not null)
                {
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(id);
                }
                else
                {
                    // The branch went away while the prompt was open (deleted, or dissolved when its last
                    // desktop moved out). The desktop exists, so let it show up on main rather than stranding it.
                    _created.Remove(id.Value);
                    _model.SyncTopRow();
                    RefreshOverlay();
                    _spatialOverlay?.SelectRoom(id);
                }
            },
            confirmLabel: "Create"));
    }

    // ── Spotlight: jump to any existing desktop, or create one named the query ─────

    // Pushed over whatever opened it (the map via Ctrl+F, or the command palette's "Jump to desktop…"
    // row), so Esc always pops straight back there.
    private void OpenSpotlight()
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any desktops deleted out from under us before offering jumps
        NavMap map = _model.BuildMap();
        var items = new List<PaletteItem>();
        int lastIndex = -1; // the last-visited row, to float to the top

        // Is this desktop the last-visited one? Decorated with "(last)" + a ↩ icon and moved first.
        bool IsLast(DesktopId? id) => _lastVisited is { } lv && id is { } tid && tid == lv;
        string Detail(string ctx, bool last) => last ? $"{ctx} · (last)" : ctx;
        string Icon(bool last) => last ? "↩" : "→";

        // Every main-timeline desktop, then every branch's desktops (branch name in the detail so
        // typing a branch name filters to its desktops). Each carries a Preview board that highlights
        // where the jump would land, shown in the middle of the palette as you move the selection.
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            int idx = i;
            DesktopId? tid = _model.PeekTopDesktop(i)?.id;
            bool last = IsLast(tid);
            items.Add(new PaletteItem(map.TopRow[i].Label, Detail("main", last), Icon(last),
                () => Jump(() => _model!.GoToTop(idx)), // no flash — the preview already showed it
                Preview: () => PreviewMap(onMain: true, topIndex: idx, branchIndex: -1, desktopIndex: -1),
                SpatialPreview: tid is { } t ? () => SpatialPreviewScene(t) : null));
            if (last) lastIndex = items.Count - 1;
        }
        foreach (NavMapBranch g in map.Branches)
        {
            int gi = g.Index;
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                int dj = j;
                DesktopId? tid = _model.PeekBranchDesktop(gi, dj)?.id;
                bool last = IsLast(tid);
                items.Add(new PaletteItem(g.Desktops[j].Label, Detail(g.Name, last), Icon(last),
                    () => Jump(() => _model!.GoToBranchDesktop(gi, dj)),
                    Preview: () => PreviewMap(onMain: false, topIndex: -1, branchIndex: gi, desktopIndex: dj),
                    SpatialPreview: tid is { } t ? () => SpatialPreviewScene(t) : null));
                if (last) lastIndex = items.Count - 1;
            }
        }

        // Float the last-visited desktop to the top so it's the default (empty-query) selection.
        if (lastIndex >= 0)
        {
            PaletteItem lastItem = items[lastIndex];
            items.RemoveAt(lastIndex);
            items.Insert(0, lastItem);
        }

        OpenPalette("Jump to or create a desktop…",
            "↑↓ move · ↵ jump/create · Esc back · blue = you are here", items,
            query => new PaletteItem($"Create desktop “{query}”", "new · main", "+",
                () => CreateAndGoToDesktop(query))); // no target tile yet — the stage shows the live board
    }

    // The spatial twin of PreviewMap: the current scene with the jump's target as the blue selection (and
    // the desktop you're on staying the green "here"), so the jump palette highlights where you'd land as a
    // room while the user is in the spatial model.
    private SpatialScene SpatialPreviewScene(DesktopId target)
        => SpatialScene.From(_model!.BuildSpatialSource(), _spatial, target);

    // Build a board snapshot that marks a specific desktop as current (for the jump palette's preview),
    // without moving the model. Rebuilds the tiles from the live map with the target highlighted and
    // centred on its own row.
    // Build a preview board for the jump palette. IsCurrent (blue) marks where you ARE now; IsHere
    // (green) marks the selected target (which defaults to the last-visited desktop). The board is
    // centred on your current position, so the green target shows the direction/distance of the jump.
    // (onMain/topIndex/branchIndex/desktopIndex describe the target row.)
    private NavMap PreviewMap(bool onMain, int topIndex, int branchIndex, int desktopIndex)
    {
        NavMap b = _model!.BuildMap();

        bool hereMain = _model.OnTop;
        int hereTop = _model.CurrentTopIndex;
        (int hereBranch, int hereDesktop) = _model.CurrentBranchDesktop ?? (-1, -1);

        var top = b.TopRow.Select((t, i) => new NavMapTile(
            t.Label,
            hereMain && i == hereTop,      // IsCurrent (blue) = you are here
            onMain && i == topIndex,       // IsHere (green) = the target
            t.WindowCount)).ToList();      // keep the at-a-glance count on the preview board
        var branches = b.Branches.Select(g => new NavMapBranch(
            g.Index, g.Name,
            g.Desktops.Select((d, j) => new NavMapTile(
                d.Label,
                !hereMain && g.Index == hereBranch && j == hereDesktop,   // blue = current
                !onMain && g.Index == branchIndex && j == desktopIndex,   // green = target
                d.WindowCount)).ToList(),
            // Keep both the current branch and the target branch bright (undimmed).
            (!hereMain && g.Index == hereBranch) || (!onMain && g.Index == branchIndex),
            g.Index == hereBranch ? hereDesktop : g.Index == branchIndex ? desktopIndex : g.Cursor)).ToList();
        int topCursor = hereMain ? hereTop : b.TopCursor;
        return new NavMap(top, topCursor, hereMain, branches, b.TopPosition);
    }

    // Create a new unbranched desktop named the query and jump straight to it.
    private void CreateAndGoToDesktop(string name)
    {
        if (_model is null || _desktops is null) return;
        DesktopId from = _desktops.Current;
        DesktopId id = _desktops.Create(name);
        _created.Add(id.Value);
        _model.SyncTopRow();
        _desktops.SwitchTo(id);
        _model.Resync(); // land the model on the freshly-created desktop
        RecordVisit(from);
        _stage?.Dismiss(); // decisive: you're now on the new desktop, so close the overlay
    }

    // Push a palette over the current surface (or as a fresh root when nothing is showing). Esc pops back.
    private void OpenPalette(string placeholder, string hint, IReadOnlyList<PaletteItem> items,
                             Func<string, PaletteItem?>? createRow = null)
    {
        _stage?.Present(new PaletteContent(placeholder, hint, items, createRow));
    }

    // Reopen the map fresh (used as the finder's "back" target when it was summoned from the map).
    // ── Command palette: same look/feel, items are commands. ────────────────────────────

    private void ToggleCommandPalette()
    {
        if (_stage?.Current is MoveContent) return; // don't stack the palette over an active move
        // Re-press toggles the palette closed — back to the surface it opened over (the map) if there is one,
        // matching what Esc does, rather than tearing the whole chain down from under it.
        if (_stage?.Current is PaletteContent)
        {
            if (_stage.HasDurableBase) _stage.Back(); else _stage.Dismiss();
            return;
        }
        // Pressed while the map is up, the chord means the same as the map's own "p": push the palette over
        // the map so back returns there, instead of replacing the map with a fresh root.
        ShowCommandPalette(overCurrent: _spatialOverlay?.IsOpen == true);
    }

    private void OpenCommandPalette() => ShowCommandPalette(overCurrent: false);

    /// <param name="overCurrent">true: push the palette over the current surface (the map), so Esc/back and a
    /// completed command return to it. false: a fresh root, so a re-press over a half-open chain resets to a
    /// clean command palette rather than stacking deeper.</param>
    private void ShowCommandPalette(bool overCurrent)
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any externally-deleted desktops so the context board is accurate

        // Show the live map behind each command ("blue = you are here") — the stage draws it in the user's
        // current model (rows or spatial). Commands with a distinct target supply their own board that
        // highlights what they'll act on (green); a null preview falls back to the stage's live board.
        var items = BuildCommands()
            .Select(c => new PaletteItem(c.Name, c.DisabledReason,
                                         c.DisabledReason is null ? "▸" : null, c.Run,
                                         Preview: c.Preview,
                                         DisabledReason: c.DisabledReason))
            .ToList();
        var palette = new PaletteContent("Run a command…",
            "↑↓ move · ↵ run · Esc back · blue = you are here", items,
            clearSearchOnShow: true); // popping back here (Esc from a command's sub-surface) lands on the full list
        if (overCurrent) _stage?.Present(palette); else _stage?.Summon(palette);
    }

    // The command registry — real commands reusing existing handlers.
    private IReadOnlyList<Command> BuildCommands()
    {
        // Commands run synchronously. Those that push another stage surface (a palette, a prompt, the map,
        // the move flow) become the current surface, so PaletteContent.Choose sees the palette is no longer
        // current and leaves the chain in place (Esc pops back through it). Terminal commands leave the
        // palette current, so Choose unwinds to the start. Either way, no flash — one surface throughout.
        // When the last check found a newer release, the palette offers to apply it directly ("Update
        // now — vX") instead of re-checking; otherwise it's a plain "Check for updates".
        bool updateReady = _lastUpdate is { Availability: UpdateAvailability.Available };
        var update = updateReady
            ? new Command($"Update now — v{_lastUpdate!.AvailableVersion}", ApplyLastUpdate)
            : new Command("Check for updates", CheckForUpdates);

        var commands = new List<Command>
        {
            new("Jump to desktop…", OpenSpotlight), // pushed over the command palette; Esc pops back to it
            new("Open map", ToggleMap),
            new("Settings", OpenSettings),
            update,
            new("New branch…", PromptNewBranch),
            // Create, preview and delete branch templates — always available (you can create the first one from here).
            new("Manage templates…", ManageTemplatesPrompt),
            // Delete-current-desktop / remove-current-branch are intentionally not commands — do them from the
            // map, where the target is visible (each tile / branch carries its own × control). Likewise
            // move-windows is triggered from the map ("m"), not from here.
            // Save / restore / reset the whole desktop+branch arrangement — one manager for all three.
            new("Layouts…", LayoutsPrompt),
            // Quit Hypertree — behind a confirm (see ExitHypertree), since it's easy to land on while
            // typing/navigating the palette (unlike the deliberate tray menu item).
            new("Exit Hypertree", ExitHypertree),
        };

        // Monitor-layout save/restore is automatic (dock/undock); the only manual entry is the diagnose +
        // trace overlay, which is a debugging aid — gated on a dev build (DevChrome.Active) so it never shows
        // in a release/installed copy. Sits just above "Exit Hypertree".
        if (DevChrome.Active)
            commands.Insert(commands.Count - 1, new Command("Monitor placement (debug)", OpenMonitorDebugOverlay,
                DisabledReason: _layout is null ? "monitor tracking unavailable" : null));

        return commands;
    }

    // ── Reset (implode): the Layouts manager's "restore the empty layout" ───────────

    // Remove every desktop but one and clear all branches — a clean slate. Guarded by a confirm.
    // Mirrors RestoreSnapshot's teardown: stand on the survivor first so the current view is never
    // yanked out from under us, then remove the rest (their windows fall back onto the survivor).
    private void Implode()
    {
        if (_model is null || _desktops is null) return;

        _stage?.Present(new ConfirmContent(
            "Implode all desktops?\nEvery desktop and branch is removed and you’re reset to a single desktop. Windows from the others move onto it. This can’t be undone.",
            DoImplode, confirmLabel: "Implode"));
    }

    private void DoImplode()
    {
        if (_model is null || _desktops is null) return;

        _model.Reconcile(); // act on the live layout, not stale/externally-deleted desktops
        IReadOnlyList<DesktopInfo> all = _desktops.List();
        if (all.Count == 0) return; // nothing to do — never strip the machine to zero desktops

        // Keep the OS's first desktop (the canonical "Desktop 1") as the survivor; everything
        // consolidates onto it.
        DesktopId survivor = all[0].Id;
        _desktops.SwitchTo(survivor);

        foreach (DesktopInfo d in all)
        {
            if (d.Id == survivor) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, survivor); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(0, Array.Empty<Branch>()); // no branches; top row re-derives to the survivor
        RefreshOrFlash();
    }

    // ── Layouts: save / restore / reset the whole desktop+branch arrangement ─────────

    // One manager for whole-layout operations, mirroring the template manager: a palette of saved layouts
    // (each previewing the arrangement it would restore) plus a "Save current layout…" row and a bottom
    // "Reset to a single desktop" row. Enter restores a layout; Del deletes it. Save/delete return here;
    // restore/reset apply and exit to the resulting layout.
    private void LayoutsPrompt() => ShowLayoutManager(refresh: false);

    // <param name="refresh">false: push the manager over the command palette (Esc pops back). true: rebuild
    // it after a save/delete taken on a card pushed over it — the card and the stale list are replaced in
    // place, keeping the command palette beneath (see ReplaceTop).</param>
    private void ShowLayoutManager(bool refresh)
    {
        if (_model is null || _snapshots is null) return;

        var items = new List<PaletteItem>
        {
            new("Save current layout…", "capture these desktops & branches", "＋", () => OpenSaveLayoutCard()),
        };
        foreach (Snapshot snapshot in _snapshots.Load())
        {
            Snapshot s = snapshot; // capture per iteration
            items.Add(new PaletteItem(s.Name, $"{s.DesktopCount} desktops · {s.Branches.Count} branches", "⟲",
                () => ConfirmRestore(s),                          // Enter restores (pushes a confirm over this palette)…
                SpatialPreview: () => SpatialSnapshot.SceneFrom(s), // preview the spatial layout you'd land in
                OnDelete: () => ConfirmDeleteLayout(s)));          // …Del deletes it (pushes a confirm)
        }
        // The reset ("restore the empty layout") sits at the bottom — greyed out when already a single desktop.
        items.Add(new PaletteItem("Reset to a single desktop", "clear every desktop & branch", "⊘",
            Implode, DisabledReason: _model.TotalDesktops <= 1 ? "already a single desktop" : null));

        // Typing a name that matches no saved layout offers to save the current one under it.
        PaletteItem? CreateRow(string q) =>
            new($"Save “{q}”", "save current layout", "＋", () => OpenSaveLayoutCard(prefillName: q));

        var palette = new PaletteContent("Layouts…",
            "↑↓ move · ↵ restore / save · ⌦ delete · Esc back · preview = the saved layout", items, CreateRow);
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    // Prompt for a name, then save the current layout under it, and return to the (refreshed) manager.
    private void OpenSaveLayoutCard(string? prefillName = null)
    {
        _stage?.Present(new PromptContent("Save layout",
            "Save the current desktops and branches under a name you can restore to later.",
            "layout name (e.g. before-refactor)",
            name => { SaveSnapshot(name); ShowLayoutManager(refresh: true); },
            confirmLabel: "Save", prefill: prefillName, selectAll: prefillName is not null));
    }

    private void SaveSnapshot(string name)
    {
        if (_model is null || _snapshots is null) return;
        _model.Reconcile(); // capture the live layout, not stale/deleted desktops

        Snapshot snap = _model.CaptureSnapshot(name);
        SpatialSnapshot.Capture(snap, _spatial); // layer the room positions & group colours on top of the structure

        // Same name overwrites, so re-snapshotting a layout updates it in place.
        var list = _snapshots.Load().Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(snap);
        _snapshots.Save(list);
    }

    private void ConfirmRestore(Snapshot snap)
    {
        if (_desktops is null) return;
        _stage?.Present(new ConfirmContent(
            $"Restore layout “{snap.Name}”?\nYour desktops are rebuilt to match it. Desktops that aren’t part of the layout are removed (any windows on them move to another desktop).",
            () => RestoreSnapshot(snap), confirmLabel: "Restore"));
    }

    // Delete a saved layout (Del on its row), then return to the (refreshed) manager.
    private void ConfirmDeleteLayout(Snapshot snap)
    {
        if (_snapshots is null) return;
        _stage?.Present(new ConfirmContent($"Delete layout “{snap.Name}”?", () =>
        {
            var list = _snapshots.Load().Where(s => !s.Name.Equals(snap.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            _snapshots.Save(list);
            ShowLayoutManager(refresh: true); // return to the manager, now without the deleted layout
        }, confirmLabel: "Delete"));
    }

    // Rebuild the OS desktops + the model to match a snapshot. Re-attaches to a saved desktop by its GUID
    // when it still exists, re-creates it by label when it doesn't, then removes anything not in the
    // snapshot. We switch to the snapshot's first desktop BEFORE any removal so the current view can't be
    // yanked out from under us.
    private void RestoreSnapshot(Snapshot snap)
    {
        if (_model is null || _desktops is null) return;
        if (snap.DesktopCount == 0) return; // nothing to restore to — never strip the machine to zero desktops

        _model.Reconcile();
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        var keep = new HashSet<Guid>();

        // Re-keying maps for the spatial facts: a captured desktop GUID → the live GUID it resolved to, and
        // a captured branch id → the fresh id its restored branch minted. Positions/colours ride these over.
        var desktopRemap = new Dictionary<Guid, Guid>();
        var branchRemap = new Dictionary<Guid, Guid>();

        // Resolve one saved desktop to a live id: reuse its GUID if present (renamed to the saved label),
        // else create a fresh desktop with that label. Branch desktops are tracked in _created so the
        // teardown guard may remove them later; main desktops are the user's and are never tracked.
        DesktopId Resolve(PersistedDesktop d, bool branch)
        {
            DesktopId id;
            if (live.Contains(d.Id))
            {
                id = new DesktopId(d.Id);
                if (!string.IsNullOrWhiteSpace(d.Label)) { try { _desktops!.Rename(id, d.Label); } catch { } }
            }
            else
            {
                id = _desktops!.Create(d.Label);
            }
            keep.Add(id.Value);
            desktopRemap[d.Id] = id.Value; // remember where this room's stored position should land
            if (branch) _created.Add(id.Value);
            return id;
        }

        // Main desktops first, so the first one exists to stand on before any removal.
        var mainIds = snap.MainDesktops.Select(d => Resolve(d, branch: false)).ToList();

        var branches = new List<Branch>(snap.Branches.Count);
        foreach (PersistedBranch pg in snap.Branches)
        {
            var refs = pg.Desktops.Select(d => new DesktopRef(Resolve(d, branch: true), d.Label)).ToList();
            if (refs.Count == 0) continue;
            var branch = new Branch(pg.Name, refs, pg.LastUsedIndex); // a template restore mints a fresh id
            branchRemap[pg.Id] = branch.Id; // remember where this group's stored colour should land
            branches.Add(branch);
        }

        // Land on the snapshot's first desktop (main[0], else the first branch desktop) before removing.
        DesktopId first = mainIds.Count > 0 ? mainIds[0] : branches[0].Desktops[0].Id;
        _desktops.SwitchTo(first);

        // Remove every desktop that isn't part of the snapshot; windows fall back to the first desktop.
        foreach (DesktopInfo d in _desktops.List().ToList())
        {
            if (keep.Contains(d.Id.Value)) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, first); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(snap.MainSlot, branches); // re-derives the top row + re-anchors to `first`

        // Re-apply the saved spatial arrangement onto the live state, re-keyed to the ids we just resolved,
        // and persist it — so the restored map lands in its 2-D layout, not the default rows. Then the map
        // rebuild below (RefreshOrFlash → BuildSpatialSource + _spatial) reflects it. Empty tables (an old
        // snapshot, or a never-arranged map) simply write nothing, leaving the defaults in place.
        SpatialSnapshot.ApplyTo(_spatial, snap, desktopRemap, branchRemap);
        _spatialStore?.Save(_spatial);

        RefreshOrFlash();
    }

    // ── Settings (tray · map cog · command palette "settings") ──────────────────────

    private void OpenSettings()
    {
        if (_activator is null || _startup is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }

        // Suspend the global hotkeys while settings is open so the rebind capture reads keystrokes cleanly
        // (an active chord like Ctrl+Alt+P mustn't fire its command while the user is pressing it to rebind).
        // They're re-registered from the (possibly changed) bindings when the window closes.
        SuspendHotkeys();

        _settingsWindow = new SettingsWindow(_settings, _startup.IsEnabled, SaveSettings, _activator,
            new UpdateHooks(CheckForUpdates, ApplyLastUpdate, () => _lastUpdate));
        _settingsWindow.Topmost = true; // sit above the map/flash if one is showing
        // Settings is the one surface that's still its own window; when it closes, re-register the hotkeys
        // (picking up any rebind) and hand the stage its key focus back so an underlying map resumes.
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_shuttingDown) return; // teardown already unregistered; don't resurrect the hotkey threads
            RegisterHotkeys();
            _stage?.Reassert();
            // A map-style change made while settings was open was deferred (see ApplyMapStyle) to avoid a
            // z-order fight; repaint the open map now the window's gone so it picks the new style up.
            RefreshOverlay();
        };
        _settingsWindow.Show();
        _settingsWindow.TakeFocus();
    }

    // Called live on every change in the settings window (there's no Save button). Persists and re-applies
    // each time; hotkey re-registration is still left to the window's Closed handler, so the global hotkeys
    // stay suspended for exactly as long as the window is open (and a rebind lands cleanly on close).
    private void SaveSettings(AppSettings settings, bool startOnLogin)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
        ApplyTaskbarLabel();
        ApplySwitcher();
        ApplyMapStyle();
        _startup?.SetEnabled(startOnLogin);
    }

    // v on the map (or the Settings selector) cycles the board style board → metro → ascii → board. It's a
    // persisted, app-wide choice, so we update the setting, save it, and push it onto the stage — every
    // surface that draws a board follows.
    private void ToggleMapStyle()
    {
        _settings.MapStyle = _settings.MapStyle switch
        {
            MapStyle.Board => MapStyle.Metro,
            MapStyle.Metro => MapStyle.Ascii,
            _ => MapStyle.Board,
        };
        _settingsStore?.Save(_settings);
        ApplyMapStyle();
    }

    // Push the current style onto the stage and repaint whatever it's showing, so the switch is immediate
    // (the interactive map re-renders; a card's backdrop refreshes behind it). A no-op when the style hasn't
    // actually changed, so live-apply toggling an unrelated setting doesn't churn the board.
    private void ApplyMapStyle()
    {
        if (_stage is null || _stage.MapStyle == _settings.MapStyle) return;
        _stage.MapStyle = _settings.MapStyle;
        // The spatial map's metrics are style-independent (only the room glyph changes), so leave its camera
        // put when it's open; only reframe when navigating with the map closed (the flash uses the offset).
        if (_spatialOverlay is not { IsOpen: true }) _mapCamera.Reframe();
        // While the Settings window is open it sits above the stage; re-rendering the map here would end in
        // _stage.BringToFront() and steal the top of the z-order from it. Defer the map repaint to the
        // Settings Closed handler; refreshing a card backdrop (no z-order change) is safe either way.
        if (_settingsWindow is null && _model is not null && _spatialOverlay is { IsOpen: true })
            _spatialOverlay.SetSource(_model.BuildSpatialSource(), _spatial);
        _stage.RefreshBackdrop();
    }

    // Position (or hide) the persistent taskbar label to match the placement setting.
    private void ApplyTaskbarLabel()
    {
        if (_taskbarLabel is null) return;
        _taskbarLabel.SetPlacement(_settings.TaskbarLabelPlacement);
    }

    // Show or hide the floating branch switcher to match the setting.
    private void ApplySwitcher()
    {
        if (_switcher is null) return;
        if (_settings.ShowSwitcher) _switcher.Enable();
        else _switcher.Disable();
    }

    // Ctrl+Alt+W — collapse the switcher to its bubble, or expand it. A no-op when the switcher is off:
    // the chord is registered regardless (like every command), but there's nothing to toggle.
    private void ToggleSwitcherCollapsed()
    {
        if (_settings.ShowSwitcher) _switcher?.ToggleCollapsed();
    }

    // A jump from the switcher: switch to the row (a branch by id, or main when null), landing on the
    // chosen desktop or — when null — the row's resume point. Reconcile first so a desktop deleted from
    // Task View since the last snapshot never traps the click (mirrors the map / CLI goto path).
    private void JumpFromSwitcher(Guid? branchId, int? desktop)
    {
        if (_model is null || _desktops is null) return;
        _model.Reconcile();
        DesktopId from = _desktops.Current;
        if (_model.GoTo(branchId, desktop, out _) != GoToResult.Ok) return;
        RecordVisit(from);
        // If the map happens to be open (it suppresses the switcher, but be safe), keep it in step.
        SyncOpenMapToCurrent();
    }

    // The switcher persists its own position (after a drag) and collapse state through these, folded into
    // the same settings file everything else uses. The expanded panel and the collapsed bubble keep separate
    // coordinates, so dragging one never moves the other.
    private void SaveSwitcherPosition(bool collapsed, Avalonia.PixelPoint at)
    {
        if (collapsed) { _settings.SwitcherCollapsedX = at.X; _settings.SwitcherCollapsedY = at.Y; }
        else { _settings.SwitcherX = at.X; _settings.SwitcherY = at.Y; }
        _settingsStore?.Save(_settings);
    }

    private void SaveSwitcherCollapsed(bool collapsed)
    {
        _settings.SwitcherCollapsed = collapsed;
        _settingsStore?.Save(_settings);
    }

    // Right-click → "Exit Hypertree": a direct shutdown, like the tray's Exit item (no overlay confirm — the
    // menu choice is already deliberate).
    private void ExitFromSwitcher()
        => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // The (branch, name) the taskbar label should show for the desktop the OS is currently on — resolved
    // by id, so it's right even after a switch made outside Hypertree. Null before startup / during teardown.
    private (string? branch, string name)? CurrentDesktopLabel()
    {
        if (_model is null || _desktops is null) return null;
        (string? branch, string label) = _model.Describe(_desktops.Current);
        return (branch, label);
    }

    // ── Software updates (tray · command palette · settings) ─────────────────────────

    // Entry point for the tray item, the command palette, and the Settings window's button. Deliberately
    // stays out of the way: nothing opens over the desktop and nothing takes the pointer — the whole flow
    // reports through Action Center notifications, and the "update available" one applies the update when
    // clicked. The Settings window, when open, mirrors the same states inline.
    private void CheckForUpdates()
    {
        _settingsWindow?.OnUpdateCheckStarted();
        // Silent: the result notification follows moments later, and a check shouldn't chime twice.
        Notify("Checking for updates…", "Looking for a newer release of Hypertree.", silent: true);
        _ = ResolveUpdateAsync();
    }

    private async Task ResolveUpdateAsync()
    {
        UpdateCheckResult result;
        try { result = await UpdateChecker.CheckDetailedAsync().ConfigureAwait(false); }
        catch { result = new UpdateCheckResult { Availability = UpdateAvailability.Failed }; }

        OnUi(() =>
        {
            // Recorded either way, so the tray menu and the palette can offer "Update now" from here on
            // without re-checking — even if the notification was never seen.
            _lastUpdate = result;
            RefreshUpdateMenuItem();
            _settingsWindow?.OnUpdateResult(result);
            NotifyUpdateResult(result);
        });
    }

    // The notification for a finished check: actionable when a newer release exists, informational otherwise.
    private void NotifyUpdateResult(UpdateCheckResult result)
    {
        switch (result.Availability)
        {
            case UpdateAvailability.Available:
                Notify($"Update available — v{result.AvailableVersion}",
                       $"You’re on v{result.CurrentVersion}. Click to download it and restart Hypertree.",
                       ApplyUpdateAction);
                break;
            case UpdateAvailability.UpToDate:
                Notify("You’re up to date", $"Hypertree v{result.CurrentVersion} is the latest release.");
                break;
            case UpdateAvailability.NotApplicable:
                Notify("Update checks need an installed build",
                       "This looks like a dev build. Install Hypertree from a GitHub release and it can update itself.");
                break;
            default:
                Notify("Couldn’t check for updates",
                       "The update feed was unreachable — check your connection and try again.");
                break;
        }
    }

    // The one action a notification of ours can carry: "the update you were told about — install it".
    private const string ApplyUpdateAction = "update";
    // Every step of the update flow reuses this key, so checking → result → downloading update one
    // notification in place instead of leaving three behind in the Action Center.
    private const string UpdateNoticeKey = "update-check";
    // The redock "restore your layout?" offer and its follow-up share one key, so the confirmation replaces
    // the offer rather than stacking beside it.
    private const string RestoreLayoutAction = "restore-monitor-layout";
    private const string MonitorLayoutNoticeKey = "monitor-layout";

    // Raise a Windows notification. Informational unless given an action, which the user can click.
    private void Notify(string title, string message, string? action = null, bool silent = false)
        => _notifier?.Show(title, message, action, silent, replaces: UpdateNoticeKey);

    // A notification click came back (on a background thread — see INotifier.Activated).
    private void OnNotificationActivated(string action)
    {
        if (action == ApplyUpdateAction) OnUi(ApplyLastUpdate);
        else if (action == RestoreLayoutAction) OnUi(RestoreCurrentSetLayout);
    }

    // The tray menu's and command palette's "Update now", the Settings install button, and a click on the
    // "update available" notification all land here: clear anything showing, say it's running, then
    // download + install (which restarts the app on success).
    private void ApplyLastUpdate()
    {
        if (_lastUpdate is not { Availability: UpdateAvailability.Available } pending) return;
        _stage?.Dismiss();
        _settingsWindow?.OnUpdateApplying();
        Notify($"Downloading v{pending.AvailableVersion}",
               "Hypertree will restart itself once the update is installed.", silent: true);
        _ = ApplyUpdateAsync(pending);
    }

    private async Task ApplyUpdateAsync(UpdateCheckResult pending)
    {
        try { await UpdateChecker.ApplyAsync(pending).ConfigureAwait(false); }
        catch
        {
            // Download/apply failed (network, locked files). The check handles are now spent, so drop the
            // remembered result — the tray and palette offer a fresh check rather than a stale "Update now".
            OnUi(() =>
            {
                _lastUpdate = null;
                RefreshUpdateMenuItem();
                _settingsWindow?.OnUpdateFailed();
                Notify("Update failed", "The download or install didn’t complete. Try checking again.");
            });
        }
        // On success the process restarts and never returns here.
    }

    // Run <paramref name="action"/> on the UI thread. The update continuations resume off a background
    // thread (ConfigureAwait(false)), so every stage/field touch is marshalled back through here.
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // Show the result of an action that completed on a card: if the chain returns to the map (its durable
    // base), prime the map so it redraws when the stage unwinds to it; otherwise the stage will dismiss, so
    // flash the result over the bare desktop instead.
    private void RefreshOrFlash()
    {
        if (_model is null) return;
        // Map in the chain (it's the durable base the card sits on): stash the fresh scene so it shows when
        // the stage unwinds back — not gated on IsOpen, since the card on top makes the map not-current.
        if (_stage is not null && _stage.HasDurableBase)
            _spatialOverlay?.SetSource(_model.BuildSpatialSource(), _spatial);
        // A result flash, not a gesture — just times out. It appears over a bare desktop, so it fades up too.
        else FlashBoard(null, HotkeyModifiers.None, move: null, animate: false, fade: WindowFx.SystemAnimationsEnabled());
    }

    // The product version for the tray header, read from the assembly (set by <Version> in the csproj) so
    // it never drifts from the real build. Trims any "+commit" build metadata the SDK may append.
    private static string AppVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
            ? attr.InformationalVersion.Split('+')[0]
            : "0.1.0");

    // Exit from the command palette, behind a confirm. On confirm we dismiss the overlay FIRST so it
    // vanishes immediately, then post the shutdown so it runs after the stage has painted itself away —
    // otherwise the full-screen overlay lingers on screen through teardown and the close reads as sluggish
    // next to the tray's Exit (which has nothing showing when it fires).
    private void ExitHypertree()
    {
        _stage?.Present(new ConfirmContent(
            "Exit Hypertree?\nYour desktops and branches are left exactly as they are — only Hypertree closes.",
            () =>
            {
                _stage?.Dismiss();
                Dispatcher.UIThread.Post(() =>
                    (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            },
            confirmLabel: "Exit"));
    }

    private void BuildTray()
    {
        // Right-click menu: a disabled version header, the two entry points, then Exit (also in the palette).
        var header = new NativeMenuItem($"Hypertree {AppVersion}") { IsEnabled = false };
        var palette = new NativeMenuItem("Open command palette");
        palette.Click += (_, _) => Dispatcher.UIThread.Post(OpenCommandPalette);
        var settings = new NativeMenuItem("Open settings");
        settings.Click += (_, _) => Dispatcher.UIThread.Post(OpenSettings);
        // Held so a finished check can retitle it — see RefreshUpdateMenuItem.
        _updateMenuItem = new NativeMenuItem("Check for updates");
        _updateMenuItem.Click += (_, _) => Dispatcher.UIThread.Post(RunUpdateMenuItem);
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu { header, new NativeMenuItemSeparator(), palette, settings, _updateMenuItem, new NativeMenuItemSeparator(), exit },
        };
        // Left-click the tray icon → open the command palette (the app's main entry point).
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ToggleCommandPalette);
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    // Stand up the Action Center notifier the update flow reports through. Constructing it registers the
    // COM activator (so a click on an "update available" toast reaches this running instance), which is
    // best-effort: if the platform refuses, the update flow simply runs without notifications and the
    // Settings window's inline status carries the detail.
    private void BuildNotifier()
    {
        try
        {
            _notifier = new WindowsToastNotifier();
            _notifier.Activated += OnNotificationActivated;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Hypertree could not start notifications: {ex.Message}");
        }
    }

    // The tray's update item does whatever its title says — apply a found release, else check. Reading
    // _lastUpdate here (rather than trusting the title) keeps the two from drifting.
    private void RunUpdateMenuItem()
    {
        if (_lastUpdate is { Availability: UpdateAvailability.Available }) ApplyLastUpdate();
        else CheckForUpdates();
    }

    // Retitle the tray's update item to match what's known. Like the command palette, it offers "Update
    // now — vX" once a check has found a newer release, so applying it doesn't re-check. (Avalonia's
    // NativeMenu has no "about to open" hook, so the item is updated when the state changes instead.)
    private void RefreshUpdateMenuItem()
    {
        if (_updateMenuItem is null) return;
        _updateMenuItem.Header = _lastUpdate is { Availability: UpdateAvailability.Available } ready
            ? $"Update now — v{ready.AvailableVersion}"
            : "Check for updates";
    }

    // ── Branch definition ─────────────────────────────────────────────────────────

    // "New branch…" — always the branch card. The card itself carries a "Load from template" button (only
    // when templates exist), so a template is an optional in-place fill, never a gate before the fields.
    private void PromptNewBranch()
    {
        if (_desktops is null) return;
        OpenNewBranchDialog(null);
    }

    // Open the branch prompt as a card, optionally pre-filled with a template's desktop labels. The card's
    // in-card "Load from template" button is wired only when there's at least one template to load.
    private void OpenNewBranchDialog(IReadOnlyList<string>? prefillLabels)
    {
        Action<Action<IReadOnlyList<string>>>? onLoadTemplate =
            _settings.BranchTemplates.Count > 0 ? PickTemplateInto : null;
        _stage?.Present(new BranchContent(CreateBranch, prefillLabels, onLoadTemplate));
    }

    // Open the template picker over the branch card. The chosen template's labels are handed to `fill`
    // (which writes them into the card's still-editable Desktops box), then we pop back to the card — so
    // you can tweak the loaded labels before creating, or Esc back with the card untouched.
    private void PickTemplateInto(Action<IReadOnlyList<string>> fill)
    {
        var items = _settings.BranchTemplates.Select(template =>
        {
            BranchTemplate t = template; // capture per iteration
            return new PaletteItem(t.Name, string.Join(" · ", t.Labels), "▸", () =>
            {
                fill(t.Labels);
                _stage?.Back(); // return to the branch card, now pre-filled
            });
        }).ToList();
        OpenPalette("Pick a template…", "↑↓ move · ↵ use · Esc back", items);
    }

    private void CreateBranch(BranchSpec spec)
    {
        if (_model is null || _desktops is null) return;

        var refs = new List<DesktopRef>(spec.Labels.Count);
        foreach (string label in spec.Labels)
        {
            DesktopId id = _desktops.Create($"{spec.Name} · {label}");
            _created.Add(id.Value);
            refs.Add(new DesktopRef(id, label));
        }

        var branch = new Branch(spec.Name, refs);
        // Created over the map (the branch prompt sits on top of it): attach the branch below the highlighted
        // room's group, not below main. Tray / command-palette creation has no map in the chain, so it falls
        // back to below main.
        if (_stage is { HasDurableBase: true } && _spatialOverlay?.SelectedRoom is { } room
            && _model.Locate(room) is { } at)
            _model.AddBranchBelow(at.onMain, at.branchIndex, branch);
        else _model.AddBranch(branch);
        RefreshOrFlash();
    }

    // ── Branch templates (reusable desktop recipes for new branches) ─────────────────

    // The single template manager: a palette listing every saved template (with a live preview of what it
    // would create) plus a "Create new template" row. Choosing a template deletes it (behind a confirm);
    // "Create new" (or typing a name that matches nothing) opens the definition card.
    private void ManageTemplatesPrompt() => ShowTemplateManager(refresh: false);

    // <param name="refresh">false: push the manager over the current surface (the command palette; Esc pops
    // back to it). true: rebuild it after a create/delete taken on a card pushed over it — the card and the
    // now-stale list are replaced in place, so the new list shows while the command palette beneath is kept
    // (Esc still returns there).</param>
    private void ShowTemplateManager(bool refresh)
    {
        if (_settingsStore is null) return;

        var items = new List<PaletteItem>
        {
            new("Create new template", "name it and list its desktops", "＋", () => OpenCreateTemplateCard()),
        };
        foreach (BranchTemplate template in _settings.BranchTemplates)
        {
            BranchTemplate t = template; // capture per iteration
            items.Add(new PaletteItem(t.Name, string.Join(" · ", t.Labels), "🗑",
                () => ConfirmDeleteTemplate(t),          // Enter deletes (pushes a confirm card over this palette)…
                Preview: () => TemplatePreview(t),        // show the branch this template would stand up
                OnDelete: () => ConfirmDeleteTemplate(t))); // …and Del does the same on the highlighted row
        }

        // Typing a name that matches no existing template offers to create it with that name pre-filled.
        PaletteItem? CreateRow(string q) =>
            new($"Create “{q}”", "new template", "＋", () => OpenCreateTemplateCard(prefillName: q));

        var palette = new PaletteContent("Manage templates…",
            "↑↓ move · ↵ create/delete · ⌦ delete · Esc back · preview = what it creates", items, CreateRow);
        // Refresh drops the card/confirm (top) + the stale manager beneath it (popCount 2), keeping the
        // command palette under that; the initial open just pushes over the command palette.
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    // Open the template-definition card, optionally pre-filled. On confirm the template is saved and we
    // land back in the (refreshed) manager, so you can immediately create or delete another.
    private void OpenCreateTemplateCard(string? prefillName = null, IReadOnlyList<string>? prefillLabels = null)
    {
        _stage?.Present(new TemplateContent((name, labels) =>
        {
            SaveTemplate(name, labels);
            ShowTemplateManager(refresh: true); // return to the manager, now including the new template
        }, prefillName, prefillLabels));
    }

    private void SaveTemplate(string name, IReadOnlyList<string> labels)
    {
        if (_settingsStore is null) return;
        // Same name overwrites, so re-saving updates a template in place (mirrors snapshots).
        _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.BranchTemplates.Add(new BranchTemplate(name, labels));
        _settingsStore.Save(_settings);
    }

    private void ConfirmDeleteTemplate(BranchTemplate template)
    {
        _stage?.Present(new ConfirmContent($"Delete template “{template.Name}”?", () =>
        {
            _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));
            _settingsStore?.Save(_settings);
            ShowTemplateManager(refresh: true); // return to the manager, now without the deleted template
        }, confirmLabel: "Delete"));
    }

    // Preview board for a template: the single branch it would add (its desktops), sitting below a stub
    // main timeline — so the manager shows, at a glance, exactly what picking this template stands up.
    private static NavMap TemplatePreview(BranchTemplate t)
    {
        var tiles = t.Labels.Select(l => new NavMapTile(l, IsCurrent: false)).ToList();
        var branch = new NavMapBranch(0, t.Name, tiles, IsCurrentLevel: true, Cursor: 0);
        return new NavMap(
            TopRow: new[] { new NavMapTile("main", IsCurrent: false) },
            TopCursor: 0, OnTop: true,
            Branches: new[] { branch },
            TopPosition: 0);
    }

    private void RemoveBranch(int index)
    {
        if (_model is null) return;
        // RemoveBranch reassigns the branch's still-live desktops onto main; TearDown then destroys them in
        // the OS. Resync re-derives the model from the live desktop list so the map reflects the destruction
        // now — without it the destroyed desktops ghost onto main until the next reconcile wipes them.
        TearDown(_model.RemoveBranch(index));
        _model.Resync();
        RefreshOrFlash();
    }

    // ── Delete a single desktop (map × badge) with a confirm prompt ───────────────

    private void DeleteTopDesktop(int index)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekTopDesktop(index);
        if (peek is null || _model.TotalDesktops <= 1) return; // never delete the last desktop

        Confirm($"Delete desktop “{peek.Value.label}”?\nAny windows on it move to another desktop.", () =>
        {
            _desktops.Remove(peek.Value.id, Fallback(peek.Value.id));
            _created.Remove(peek.Value.id.Value);
            _model.Resync();
            RefreshOrFlash();
        });
    }

    private void DeleteBranchDesktop(int branchIndex, int desktopIndex)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekBranchDesktop(branchIndex, desktopIndex);
        if (peek is null || _model.TotalDesktops <= 1) return;

        // Name the branch in the prompt: a label like "api" says nothing about which branch it sits in, and
        // the same label commonly repeats across branches (that's the point of templates).
        NavMapBranch g = _model.BuildMap().Branches[branchIndex];
        // Taking a branch's only desktop takes the branch with it (see DetachBranchDesktop), which is a
        // bigger deal than the prompt would otherwise let on.
        string consequence = g.Desktops.Count == 1
            ? $"It’s the only desktop in “{g.Name}”, so the branch goes too. Any windows on it move to another desktop."
            : "Any windows on it move to another desktop.";

        Confirm($"Delete desktop “{peek.Value.label}” from branch “{g.Name}”?\n{consequence}", () =>
        {
            DesktopId fallback = Fallback(peek.Value.id);
            DesktopId? id = _model.DetachBranchDesktop(branchIndex, desktopIndex);
            if (id is not null)
            {
                _created.Remove(id.Value.Value);
                try { _desktops.Remove(id.Value, fallback); } catch { /* already gone */ }
            }
            _model.Resync();
            RefreshOrFlash();
        });
    }

    // Any live desktop other than the one being deleted (prefer the current view).
    private DesktopId Fallback(DesktopId avoid)
    {
        DesktopId cur = _desktops!.Current;
        if (cur != avoid) return cur;
        foreach (DesktopInfo d in _desktops.List()) if (d.Id != avoid) return d.Id;
        return avoid; // unreachable — guarded by TotalDesktops > 1
    }

    // A confirm card pushed over the current surface (the map, when a Del/Shift+Del came from it). Esc pops
    // back; confirming runs the action then unwinds to where the chain started.
    private void Confirm(string message, Action onConfirm)
        => _stage?.Present(new ConfirmContent(message, onConfirm));

    // Remove a branch's desktops — but ONLY ones Hypertree created, never the user's own desktops.
    private void TearDown(Branch? branch)
    {
        if (branch is null || _model is null || _desktops is null) return;
        DesktopId fallback = _model.FallbackDesktopId;
        foreach (DesktopRef d in branch.Desktops)
        {
            if (_created.Remove(d.Id.Value))
            {
                try { _desktops.Remove(d.Id, fallback); } catch { /* already gone — best-effort */ }
            }
        }
    }

    private void Teardown()
    {
        _shuttingDown = true; // stop the settings window's Closed handler from re-registering hotkeys on exit
        _gesturePoll?.Stop();
        _layoutTimer?.Stop();
        _watcher?.Dispose();
        _control?.Dispose();  // stop accepting before the status file says we've gone
        _status?.Dispose();   // deletes status.json, so nothing reports a tray that isn't here
        SuspendHotkeys();
        if (_tray is not null) _tray.IsVisible = false;
        _monitorDebugWindow?.Close();
        _stage?.Close(); // closes the shared host + dims (map / palettes / prompts / move all live here)
        _hud?.Close();
        _taskbarLabel?.Close();
        _switcher?.Close();
        _settingsWindow?.Close();
        _changelogWindow?.Close();
    }
}
