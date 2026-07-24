using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Store;

namespace Hypertree.App;

/// <summary>
/// The tray/HUD/hotkey shell. Builds the desktop controller and navigation model (top row = the
/// existing ungrouped desktops; groups are created at runtime), wires the Ctrl+Alt+Arrow nav hotkeys
/// and the Ctrl+Alt+Space map toggle, and flashes the board on navigation. Tray-only, outlives its
/// windows (ShutdownMode.OnExplicitShutdown).
/// </summary>
public sealed class App : Application
{
    // Ctrl+Alt+Arrow — the default layer from M0 (Win+Ctrl+Arrow is the native desktop switch).
    // Down = dive, Up = surface, Left/Right = within the current level.
    private const HotkeyModifiers Mods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private static readonly (HotkeyKey key, NavAction action)[] Bindings =
    {
        (HotkeyKey.ArrowDown,  NavAction.Dive),
        (HotkeyKey.ArrowUp,    NavAction.Surface),
        (HotkeyKey.ArrowLeft,  NavAction.MoveLeft),
        (HotkeyKey.ArrowRight, NavAction.MoveRight),
    };
    private const HotkeyKey MapKey = HotkeyKey.Space; // Ctrl+Alt+Space toggles the map overlay
    private const HotkeyKey PaletteKey = HotkeyKey.P; // Ctrl+Alt+P spotlight; Ctrl+Alt+Shift+P command palette

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree created (for groups). Only these are ever torn down — the top row is the
    // user's own desktops and must never be removed.
    private readonly HashSet<Guid> _created = new();
    private IDesktopController? _desktops;
    private IForegroundActivator? _activator;
    private IStartupManager? _startup;
    private NavigationModel? _model;
    private HudWindow? _hud;
    private MapOverlay? _overlay;
    private TrayIcon? _tray;
    private ScopeDialog? _dialog;
    private PaletteWindow? _palette;
    private SettingsWindow? _settingsWindow;
    private NameDialog? _nameDialog;
    private ISettingsStore? _settingsStore;
    private ISnapshotStore? _snapshots;
    private AppSettings _settings = new();

    // "Last visited" = the desktop you came from, committed when a navigation completes (Ctrl+Alt
    // released) or on a discrete jump. Surfaced first in the jump palette so you can hop back.
    private DesktopId? _lastVisited;
    private DesktopId? _gestureFrom; // where the in-progress keyboard gesture started
    private DispatcherTimer? _gesturePoll;

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
        _model = new NavigationModel(_desktops, new FileStateStore());
        // Desktops restored from persisted groups were created by Hypertree — track them so the
        // teardown guard still only ever destroys our own desktops.
        foreach (DesktopId id in _model.GroupDesktopIds()) _created.Add(id.Value);

        _settingsStore = new FileSettingsStore();
        _settings = _settingsStore.Load();
        _snapshots = new FileSnapshotStore();

        _hud = new HudWindow();
        ApplyFlashSettings();

        _overlay = new MapOverlay(_desktops);
        _overlay.GoToTopRequested += i => Jump(() => _model!.GoToTop(i));
        _overlay.GoToGroupRequested += (g, d) => Jump(() => _model!.GoToGroupDesktop(g, d));
        _overlay.DeleteTopRequested += DeleteTopDesktop;
        _overlay.DeleteGroupDesktopRequested += DeleteGroupDesktop;
        _overlay.DeleteCurrentRequested += DeleteCurrentDesktop;
        _overlay.NewGroupRequested += PromptNewGroup;
        _overlay.RemoveGroupRequested += RemoveGroup;
        _overlay.SettingsRequested += OpenSettings;

        RegisterHotkeys();
        BuildTray();
    }

    private void RegisterHotkeys()
    {
        foreach (var (key, action) in Bindings)
        {
            var hk = PlatformServices.CreateGlobalHotkey();
            bool ok = hk.Register(Mods, key, () => Dispatcher.UIThread.Post(() => Navigate(action)));
            if (ok) _hotkeys.Add(hk);
            else { hk.Dispose(); Console.Error.WriteLine($"Hotkey Ctrl+Alt+{key} was refused by the OS."); }
        }

        var mapHk = PlatformServices.CreateGlobalHotkey();
        if (mapHk.Register(Mods, MapKey, () => Dispatcher.UIThread.Post(ToggleMap))) _hotkeys.Add(mapHk);
        else { mapHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+Space (map) was refused by the OS."); }

        // Ctrl+Alt+P — spotlight (jump/create desktop).
        var spotHk = PlatformServices.CreateGlobalHotkey();
        if (spotHk.Register(Mods, PaletteKey, () => Dispatcher.UIThread.Post(ToggleSpotlight))) _hotkeys.Add(spotHk);
        else { spotHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+P (spotlight) was refused by the OS."); }

        // Ctrl+Alt+Shift+P — command palette.
        var cmdHk = PlatformServices.CreateGlobalHotkey();
        if (cmdHk.Register(Mods | HotkeyModifiers.Shift, PaletteKey, () => Dispatcher.UIThread.Post(ToggleCommandPalette))) _hotkeys.Add(cmdHk);
        else { cmdHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+Shift+P (command palette) was refused by the OS."); }
    }

    // Navigate. While the map overlay is open it stays open (its windows are pinned across the
    // desktop switch) and just refreshes; otherwise the transient flash shows.
    private void Navigate(NavAction action)
    {
        if (_model is null || _desktops is null) return;
        // Start of a gesture: remember where we came from, so releasing Ctrl+Alt can record it as
        // "last visited". A poll watches for the release (works whether flashing or in the map).
        _gestureFrom ??= _desktops.Current;
        _model.Apply(action);
        if (_overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap());
        StartGesturePoll();
    }

    private void StartGesturePoll()
    {
        if (_gesturePoll is null)
        {
            _gesturePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _gesturePoll.Tick += (_, _) => { if (!CtrlAltHeld()) CompleteGesture(); };
        }
        if (!_gesturePoll.IsEnabled) _gesturePoll.Start();
    }

    // The gesture is over once Ctrl+Alt is released: if we actually moved, the desktop we started on
    // becomes "last visited".
    private void CompleteGesture()
    {
        _gesturePoll?.Stop();
        if (_gestureFrom is { } from && _desktops is not null && _desktops.Current != from)
            _lastVisited = from;
        _gestureFrom = null;
    }

    // A discrete jump (palette / map click): record where we came from immediately.
    private void Jump(Func<bool> doJump)
    {
        if (_desktops is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        if (_desktops.Current != from) _lastVisited = from;
        RefreshOverlay();
    }

    private const int VK_CONTROL = 0x11, VK_MENU = 0x12;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool CtrlAltHeld()
        => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    private void ToggleMap()
    {
        if (_model is null || _overlay is null) return;
        if (_overlay.IsOpen) { _overlay.Close(); return; }

        _model.Reconcile(); // drop any externally-deleted desktops before showing the map
        _overlay.Open(_model.BuildMap()); // vertical model renders the stack around main — no reorder
    }

    private void RefreshOverlay()
    {
        if (_model is not null && _overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
    }

    // ── Spotlight (F4): jump to any existing desktop, or create one named the query ─────

    private void ToggleSpotlight()
    {
        if (_model is null || _activator is null) return;
        if (_palette is not null) { _palette.Close(); return; } // re-press toggles closed

        _model.Reconcile(); // drop any desktops deleted out from under us before offering jumps
        NavMap map = _model.BuildMap();
        var items = new List<PaletteItem>();
        int lastIndex = -1; // the last-visited row, to float to the top

        // Is this desktop the last-visited one? Decorated with "(last)" + a ↩ icon and moved first.
        bool IsLast(DesktopId? id) => _lastVisited is { } lv && id is { } tid && tid == lv;
        string Detail(string ctx, bool last) => last ? $"{ctx} · (last)" : ctx;
        string Icon(bool last) => last ? "↩" : "→";

        // Every main-timeline desktop, then every group's desktops (group name in the detail so
        // typing a group name filters to its desktops). Each carries a Preview board that highlights
        // where the jump would land, shown in the middle of the palette as you move the selection.
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            int idx = i;
            bool last = IsLast(_model.PeekTopDesktop(i)?.id);
            items.Add(new PaletteItem(map.TopRow[i].Label, Detail("main", last), Icon(last),
                () => Jump(() => _model!.GoToTop(idx)), // no flash — the preview already showed it
                Preview: () => PreviewMap(onMain: true, topIndex: idx, groupIndex: -1, desktopIndex: -1)));
            if (last) lastIndex = items.Count - 1;
        }
        foreach (NavMapGroup g in map.Groups)
        {
            int gi = g.Index;
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                int dj = j;
                bool last = IsLast(_model.PeekGroupDesktop(gi, dj)?.id);
                items.Add(new PaletteItem(g.Desktops[j].Label, Detail(g.Name, last), Icon(last),
                    () => Jump(() => _model!.GoToGroupDesktop(gi, dj)),
                    Preview: () => PreviewMap(onMain: false, topIndex: -1, groupIndex: gi, desktopIndex: dj)));
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

        OpenPalette("Jump to or create a desktop…", "↑↓ move · ↵ jump/create · Esc close · blue = you are here", items,
            query => new PaletteItem($"Create desktop “{query}”", "new · main", "+",
                () => CreateAndGoToDesktop(query),
                Preview: () => _model!.BuildMap()), // no target tile yet — show the current board
            previewMode: true);
    }

    // Build a board snapshot that marks a specific desktop as current (for the jump palette's preview),
    // without moving the model. Rebuilds the tiles from the live map with the target highlighted and
    // centred on its own row.
    // Build a preview board for the jump palette. IsCurrent (blue) marks where you ARE now; IsHere
    // (green) marks the selected target (which defaults to the last-visited desktop). The board is
    // centred on your current position, so the green target shows the direction/distance of the jump.
    // (onMain/topIndex/groupIndex/desktopIndex describe the target row.)
    private NavMap PreviewMap(bool onMain, int topIndex, int groupIndex, int desktopIndex)
    {
        NavMap b = _model!.BuildMap();

        bool hereMain = _model.OnTop;
        int hereTop = _model.CurrentTopIndex;
        (int hereGroup, int hereDesktop) = _model.CurrentGroupDesktop ?? (-1, -1);

        var top = b.TopRow.Select((t, i) => new NavMapTile(
            t.Label,
            hereMain && i == hereTop,      // IsCurrent (blue) = you are here
            onMain && i == topIndex,       // IsHere (green) = the target
            t.WindowCount)).ToList();      // keep the at-a-glance count on the preview board
        var groups = b.Groups.Select(g => new NavMapGroup(
            g.Index, g.Name,
            g.Desktops.Select((d, j) => new NavMapTile(
                d.Label,
                !hereMain && g.Index == hereGroup && j == hereDesktop,   // blue = current
                !onMain && g.Index == groupIndex && j == desktopIndex,   // green = target
                d.WindowCount)).ToList(),
            // Keep both the current group and the target group bright (un-rested).
            (!hereMain && g.Index == hereGroup) || (!onMain && g.Index == groupIndex),
            g.Index == hereGroup ? hereDesktop : g.Index == groupIndex ? desktopIndex : g.Cursor)).ToList();
        int topCursor = hereMain ? hereTop : b.TopCursor;
        return new NavMap(top, topCursor, hereMain, groups, b.TopPosition);
    }

    // Create a new ungrouped desktop named the query and jump straight to it.
    private void CreateAndGoToDesktop(string name)
    {
        if (_model is null || _desktops is null) return;
        DesktopId from = _desktops.Current;
        DesktopId id = _desktops.Create(name);
        _created.Add(id.Value);
        _model.SyncTopRow();
        _desktops.SwitchTo(id);
        _model.Resync(); // land the model on the freshly-created desktop
        if (_desktops.Current != from) _lastVisited = from;
        RefreshOverlay(); // no flash — the jump/create is decisive on its own
    }

    private void OpenPalette(string placeholder, string hint, IReadOnlyList<PaletteItem> items,
                             Func<string, PaletteItem?>? createRow = null, bool previewMode = false)
    {
        if (_activator is null) return;
        _palette = new PaletteWindow(placeholder, hint, items, _activator, createRow, previewMode);
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
        _palette.TakeFocus();
    }

    // ── Command palette (F5): same look/feel, items are commands. Bones only. ───────────

    private void ToggleCommandPalette()
    {
        if (_activator is null) return;
        if (_palette is not null) { _palette.Close(); return; } // re-press toggles closed

        var items = BuildCommands()
            .Select(c => new PaletteItem(c.Name, null, "▸", c.Run))
            .ToList();
        OpenPalette("Run a command…", "↑↓ move · ↵ run · Esc close", items);
    }

    // The command registry. A few real commands (reusing existing handlers) plus stubs for features
    // that don't exist yet — the exact set isn't the point this iteration, the wiring is.
    private IReadOnlyList<Command> BuildCommands()
    {
        Action stub(string name) => () => Console.Error.WriteLine($"Command “{name}” is not implemented yet.");
        return new List<Command>
        {
            // Post so this command's palette finishes closing (clearing _palette) before the
            // spotlight opens — otherwise ToggleSpotlight would see the open palette and toggle it shut.
            new("Jump to desktop…", () => Dispatcher.UIThread.Post(ToggleSpotlight)),
            new("Settings", OpenSettings),
            new("New group…", PromptNewGroup),
            new("Delete current desktop", DeleteCurrentDesktop),
            new("Remove current group", RemoveCurrentGroup),
            // Post so this command's palette finishes closing before the prompt/palette opens.
            new("Snapshot layout…", () => Dispatcher.UIThread.Post(PromptSnapshot)),
            new("Restore snapshot…", () => Dispatcher.UIThread.Post(RestoreSnapshotPrompt)),
            new("Add branch", stub("Add branch")),          // → M2 git
            new("Move desktop to group…", stub("Move desktop to group…")),
        };
    }

    private void RemoveCurrentGroup()
    {
        if (_model is null) return;
        int index = _model.CurrentGroupIndex;
        if (index >= 0) RemoveGroup(index);
    }

    // ── Snapshots: capture the whole layout under a name, restore it later ─────────

    // Prompt for a name, then save the current layout (main timeline + groups) under it.
    private void PromptSnapshot()
    {
        if (_model is null || _snapshots is null) return;
        if (_nameDialog is not null) { _nameDialog.Activate(); return; }

        _nameDialog = new NameDialog("Snapshot layout",
            "Save the current desktops and groups under a name you can restore to later.",
            "snapshot name (e.g. before-refactor)");
        _nameDialog.Closed += (_, _) => _nameDialog = null;
        _nameDialog.Confirmed += SaveSnapshot;
        _nameDialog.Show();
    }

    private void SaveSnapshot(string name)
    {
        if (_model is null || _snapshots is null) return;
        _model.Reconcile(); // capture the live layout, not stale/deleted desktops

        // Same name overwrites, so re-snapshotting a layout updates it in place.
        var list = _snapshots.Load().Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(_model.CaptureSnapshot(name));
        _snapshots.Save(list);
    }

    // Show a palette of saved snapshots; choosing one confirms, then restores.
    private void RestoreSnapshotPrompt()
    {
        if (_snapshots is null || _activator is null) return;
        if (_palette is not null) { _palette.Close(); return; } // re-invoke toggles closed

        IReadOnlyList<Snapshot> snaps = _snapshots.Load();
        var items = snaps.Select(s => new PaletteItem(
            s.Name,
            $"{s.DesktopCount} desktops · {s.Groups.Count} groups",
            "⟲",
            () => Dispatcher.UIThread.Post(() => ConfirmRestore(s)))) // let the palette close first
            .ToList();

        OpenPalette(snaps.Count == 0 ? "No snapshots saved yet" : "Restore a snapshot…",
                    "↑↓ move · ↵ restore · Esc close", items);
    }

    private void ConfirmRestore(Snapshot snap)
    {
        var dlg = new ConfirmDialog(
            $"Restore snapshot “{snap.Name}”?\nYour desktops are rebuilt to match it. Desktops that aren’t part of the snapshot are removed (any windows on them move to another desktop).",
            confirmLabel: "Restore");
        dlg.Confirmed += () => RestoreSnapshot(snap);
        dlg.Show();
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

        // Resolve one saved desktop to a live id: reuse its GUID if present (renamed to the saved label),
        // else create a fresh desktop with that label. Group desktops are tracked in _created so the
        // teardown guard may remove them later; main desktops are the user's and are never tracked.
        DesktopId Resolve(PersistedDesktop d, bool group)
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
            if (group) _created.Add(id.Value);
            return id;
        }

        // Main desktops first, so the first one exists to stand on before any removal.
        var mainIds = snap.MainDesktops.Select(d => Resolve(d, group: false)).ToList();

        var groups = new List<Group>(snap.Groups.Count);
        foreach (PersistedGroup pg in snap.Groups)
        {
            var refs = pg.Desktops.Select(d => new DesktopRef(Resolve(d, group: true), d.Label)).ToList();
            if (refs.Count > 0) groups.Add(new Group(pg.Name, refs, pg.LastUsedIndex));
        }

        // Land on the snapshot's first desktop (main[0], else the first group desktop) before removing.
        DesktopId first = mainIds.Count > 0 ? mainIds[0] : groups[0].Desktops[0].Id;
        _desktops.SwitchTo(first);

        // Remove every desktop that isn't part of the snapshot; windows fall back to the first desktop.
        foreach (DesktopInfo d in _desktops.List().ToList())
        {
            if (keep.Contains(d.Id.Value)) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, first); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(snap.MainSlot, groups); // re-derives the top row + re-anchors to `first`
        RefreshOrFlash();
    }

    // ── Settings (tray · map cog · command palette "settings") ──────────────────────

    private void OpenSettings()
    {
        if (_activator is null || _startup is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }

        _settingsWindow = new SettingsWindow(_settings, _startup.IsEnabled, SaveSettings, _activator);
        _settingsWindow.Topmost = true; // sit above the map/flash if one is showing
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.TakeFocus();
    }

    private void SaveSettings(AppSettings settings, bool startOnLogin)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
        ApplyFlashSettings();
        _startup?.SetEnabled(startOnLogin);
    }

    private void ApplyFlashSettings()
        => _hud?.Configure(_settings.FlashHoldToKeep, _settings.FlashGraceMs, _settings.FlashTimeoutMs);

    private void RefreshOrFlash()
    {
        if (_model is null) return;
        if (_overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap());
    }

    private void BuildTray()
    {
        var header = new NativeMenuItem("Hypertree 0.1.0") { IsEnabled = false };
        var map = new NativeMenuItem("Open map (Ctrl+Alt+Space)");
        map.Click += (_, _) => ToggleMap();
        var newGroup = new NativeMenuItem("New group…");
        newGroup.Click += (_, _) => PromptNewGroup();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu { header, new NativeMenuItemSeparator(), map, newGroup, settings, new NativeMenuItemSeparator(), exit },
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    // ── Group definition (M1 stand-in for M2's git-worktree flow) ─────────────────

    private void PromptNewGroup()
    {
        if (_desktops is null) return;
        if (_dialog is not null) { _dialog.Activate(); return; }

        _dialog = new ScopeDialog();
        if (_overlay is { IsOpen: true }) _dialog.Topmost = true; // sit above the dimmed overlay
        _dialog.Closed += (_, _) => _dialog = null;
        _dialog.Confirmed += CreateGroup;
        _dialog.Show();
    }

    private void CreateGroup(ScopeSpec spec)
    {
        if (_model is null || _desktops is null) return;

        var refs = new List<DesktopRef>(spec.Labels.Count);
        foreach (string label in spec.Labels)
        {
            DesktopId id = _desktops.Create($"{spec.Name} · {label}");
            _created.Add(id.Value);
            refs.Add(new DesktopRef(id, label));
        }

        _model.AddGroup(new Group(spec.Name, refs));
        RefreshOrFlash();
    }

    private void RemoveGroup(int index)
    {
        if (_model is null) return;
        TearDown(_model.RemoveGroup(index));
        RefreshOrFlash();
    }

    // ── Delete a single desktop (map × badge) with a confirm prompt ───────────────

    // Delete whatever desktop is currently selected (footer button).
    private void DeleteCurrentDesktop()
    {
        if (_model is null) return;
        if (_model.OnTop) DeleteTopDesktop(_model.CurrentTopIndex);
        else if (_model.CurrentGroupDesktop is { } sel) DeleteGroupDesktop(sel.group, sel.desktop);
    }

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

    private void DeleteGroupDesktop(int groupIndex, int desktopIndex)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekGroupDesktop(groupIndex, desktopIndex);
        if (peek is null || _model.TotalDesktops <= 1) return;

        Confirm($"Delete desktop “{peek.Value.label}”?\nAny windows on it move to another desktop.", () =>
        {
            DesktopId fallback = Fallback(peek.Value.id);
            DesktopId? id = _model.DetachGroupDesktop(groupIndex, desktopIndex);
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

    private void Confirm(string message, Action onConfirm)
    {
        var dlg = new ConfirmDialog(message);
        if (_overlay is { IsOpen: true }) dlg.Topmost = true;
        dlg.Confirmed += onConfirm;
        dlg.Show();
    }

    // Remove a group's desktops — but ONLY ones Hypertree created, never the user's own desktops.
    private void TearDown(Group? group)
    {
        if (group is null || _model is null || _desktops is null) return;
        DesktopId fallback = _model.FallbackDesktopId;
        foreach (DesktopRef d in group.Desktops)
        {
            if (_created.Remove(d.Id.Value))
            {
                try { _desktops.Remove(d.Id, fallback); } catch { /* already gone — best-effort */ }
            }
        }
    }

    private void Teardown()
    {
        _gesturePoll?.Stop();
        foreach (var hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
        if (_tray is not null) _tray.IsVisible = false;
        _dialog?.Close();
        _overlay?.Close();
        _hud?.Close();
        _palette?.Close();
        _nameDialog?.Close();
        _settingsWindow?.Close();
    }
}
