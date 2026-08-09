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
    // controller that snapshots windows-per-monitor and puts them back across a dock cycle. Owns its
    // service, poll timer and debug overlay; see MonitorLayoutController.
    private MonitorLayoutController? _monitorLayout;

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
        // The notifier is built later in Startup (BuildNotifier), so the controller reads it through an
        // accessor rather than a captured value — matching the original "_notifier?.Show at call time".
        _monitorLayout = new MonitorLayoutController(_desktops, _activator, () => _notifier);
        _monitorLayout.Start();
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

    // Raise a Windows notification. Informational unless given an action, which the user can click.
    private void Notify(string title, string message, string? action = null, bool silent = false)
        => _notifier?.Show(title, message, action, silent, replaces: UpdateNoticeKey);

    // A notification click came back (on a background thread — see INotifier.Activated).
    private void OnNotificationActivated(string action)
    {
        if (action == ApplyUpdateAction) OnUi(ApplyLastUpdate);
        else if (action == MonitorLayoutController.RestoreAction) OnUi(() => _monitorLayout?.RestoreCurrentSetLayout());
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

    private void Teardown()
    {
        _shuttingDown = true; // stop the settings window's Closed handler from re-registering hotkeys on exit
        _gesturePoll?.Stop();
        _monitorLayout?.Dispose(); // stops the poll timer and closes the debug overlay
        _watcher?.Dispose();
        _control?.Dispose();  // stop accepting before the status file says we've gone
        _status?.Dispose();   // deletes status.json, so nothing reports a tray that isn't here
        SuspendHotkeys();
        if (_tray is not null) _tray.IsVisible = false;
        _stage?.Close(); // closes the shared host + dims (map / palettes / prompts / move all live here)
        _hud?.Close();
        _taskbarLabel?.Close();
        _switcher?.Close();
        _settingsWindow?.Close();
        _changelogWindow?.Close();
    }
}
