using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;
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

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree created (for groups). Only these are ever torn down — the top row is the
    // user's own desktops and must never be removed.
    private readonly HashSet<Guid> _created = new();
    private IDesktopController? _desktops;
    private NavigationModel? _model;
    private HudWindow? _hud;
    private MapOverlay? _overlay;
    private TrayIcon? _tray;
    private ScopeDialog? _dialog;

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
        _model = new NavigationModel(_desktops, new FileStateStore());
        // Desktops restored from persisted groups were created by Hypertree — track them so the
        // teardown guard still only ever destroys our own desktops.
        foreach (DesktopId id in _model.GroupDesktopIds()) _created.Add(id.Value);

        _hud = new HudWindow();

        _overlay = new MapOverlay(_desktops);
        _overlay.GoToTopRequested += i => { _model!.GoToTop(i); RefreshOverlay(); };
        _overlay.GoToGroupRequested += (g, d) => { _model!.GoToGroupDesktop(g, d); RefreshOverlay(); };
        _overlay.DeleteTopRequested += DeleteTopDesktop;
        _overlay.DeleteGroupDesktopRequested += DeleteGroupDesktop;
        _overlay.DeleteCurrentRequested += DeleteCurrentDesktop;
        _overlay.NewGroupRequested += PromptNewGroup;
        _overlay.RemoveGroupRequested += RemoveGroup;

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
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu { header, new NativeMenuItemSeparator(), map, newGroup, new NativeMenuItemSeparator(), exit },
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
    }
}
