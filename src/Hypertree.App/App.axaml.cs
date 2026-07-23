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
    private ISettingsStore? _settingsStore;
    private AppSettings _settings = new();

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

        _hud = new HudWindow();
        ApplyFlashSettings();

        _overlay = new MapOverlay(_desktops);
        _overlay.GoToTopRequested += i => { _model!.GoToTop(i); RefreshOverlay(); };
        _overlay.GoToGroupRequested += (g, d) => { _model!.GoToGroupDesktop(g, d); RefreshOverlay(); };
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
        if (_model is null) return;
        _model.Apply(action);
        if (_overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap());
    }

    private void ToggleMap()
    {
        if (_model is null || _overlay is null) return;
        if (_overlay.IsOpen) { _overlay.Close(); return; }

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

        NavMap map = _model.BuildMap();
        var items = new List<PaletteItem>();

        // Every main-timeline desktop, then every group's desktops (group name in the detail so
        // typing a group name filters to its desktops).
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            int idx = i;
            items.Add(new PaletteItem(map.TopRow[i].Label, "main", "→", () => { _model.GoToTop(idx); RefreshOrFlash(); }));
        }
        foreach (NavMapGroup g in map.Groups)
        {
            int gi = g.Index;
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                int dj = j;
                items.Add(new PaletteItem(g.Desktops[j].Label, g.Name, "→",
                    () => { _model.GoToGroupDesktop(gi, dj); RefreshOrFlash(); }));
            }
        }

        OpenPalette("Jump to or create a desktop…", "↑↓ move · ↵ jump/create · Esc close", items,
            query => new PaletteItem($"Create desktop “{query}”", "new · main", "+", () => CreateAndGoToDesktop(query)));
    }

    // Create a new ungrouped desktop named the query and jump straight to it.
    private void CreateAndGoToDesktop(string name)
    {
        if (_model is null || _desktops is null) return;
        DesktopId id = _desktops.Create(name);
        _created.Add(id.Value);
        _model.SyncTopRow();
        _desktops.SwitchTo(id);
        _model.Resync(); // land the model on the freshly-created desktop
        RefreshOrFlash();
    }

    private void OpenPalette(string placeholder, string hint, IReadOnlyList<PaletteItem> items,
                             Func<string, PaletteItem?>? createRow = null)
    {
        if (_activator is null) return;
        _palette = new PaletteWindow(placeholder, hint, items, _activator, createRow);
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
            new("Settings", OpenSettings),
            new("New group…", PromptNewGroup),
            new("Delete current desktop", DeleteCurrentDesktop),
            new("Remove current group", RemoveCurrentGroup),
            new("Snapshot layout", stub("Snapshot layout")),
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
        foreach (var hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
        if (_tray is not null) _tray.IsVisible = false;
        _dialog?.Close();
        _overlay?.Close();
        _hud?.Close();
        _palette?.Close();
        _settingsWindow?.Close();
    }
}
