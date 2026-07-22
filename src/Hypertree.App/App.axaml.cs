using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;
using HtScope = Hypertree.Scopes.Scope;

namespace Hypertree.App;

/// <summary>
/// The tray/HUD/hotkey shell. On startup it builds the desktop controller, imposes the (M1
/// hard-coded, non-destructive) topology over the existing desktops, wires the four Ctrl+Alt+Arrow
/// hotkeys to the navigation model, and flashes the HUD on every navigation. Tray-only — no main
/// window; the app outlives its windows (ShutdownMode.OnExplicitShutdown), like perch.
/// </summary>
public sealed class App : Application
{
    // Ctrl+Alt+Arrow — the default layer chosen in M0 (Win+Ctrl+Arrow is reserved by the native
    // desktop switch). Config-driven/rebindable is M3; the intent mapping is what matters here:
    // Down = dive (the new depth axis), Up = surface, Left/Right = within-level.
    private const HotkeyModifiers Mods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private static readonly (HotkeyKey key, NavAction action)[] Bindings =
    {
        (HotkeyKey.ArrowDown,  NavAction.Dive),
        (HotkeyKey.ArrowUp,    NavAction.Surface),
        (HotkeyKey.ArrowLeft,  NavAction.MoveLeft),
        (HotkeyKey.ArrowRight, NavAction.MoveRight),
    };
    // Ctrl+Alt+Space toggles the interactive map/config overlay.
    private const HotkeyKey MapKey = HotkeyKey.Space;

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree itself created (for scopes). Only these are ever torn down — the demo
    // topology maps onto the user's PRE-EXISTING desktops, which must never be removed.
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
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows

            // Offscreen board render for design verification: `hypertree --shot <dir>`.
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
                // Nothing else can work without the controller; surface and bail cleanly.
                Console.Error.WriteLine($"Hypertree failed to start: {ex}");
                desktop.Shutdown(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Startup()
    {
        _desktops = PlatformServices.CreateDesktopController();
        Topology topology = DemoTopology.Build(_desktops.List());
        _model = new NavigationModel(topology, _desktops);

        _hud = new HudWindow();
        // HUD flashing is driven explicitly (Navigate / scope actions) so edge no-ops still show.

        _overlay = new MapOverlay();
        _overlay.AddScopeRequested += index => PromptNewScope(index);
        _overlay.RemoveScopeRequested += index => RemoveScope(index);

        RegisterHotkeys();
        BuildTray();
    }

    private void RegisterHotkeys()
    {
        foreach (var (key, action) in Bindings)
        {
            var hk = PlatformServices.CreateGlobalHotkey();
            // Callback fires on the hotkey's own thread — marshal to the UI thread before touching
            // the model or the shell COM (both are UI-thread affine).
            bool ok = hk.Register(Mods, key, () => Dispatcher.UIThread.Post(() => Navigate(action)));
            if (ok) _hotkeys.Add(hk);
            else { hk.Dispose(); Console.Error.WriteLine($"Hotkey Ctrl+Alt+{key} was refused by the OS."); }
        }

        var mapHk = PlatformServices.CreateGlobalHotkey();
        if (mapHk.Register(Mods, MapKey, () => Dispatcher.UIThread.Post(ToggleMap))) _hotkeys.Add(mapHk);
        else { mapHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+Space (map) was refused by the OS."); }
    }

    // Apply the intent, then always flash the HUD map — so even a no-op at an edge still confirms
    // "where am I" (the load-bearing promise of PLAN.md §9 risk 3). Pressing a nav key while the
    // interactive overlay is open closes it first (arrows exit the map and navigate).
    private void Navigate(NavAction action)
    {
        if (_model is null || _hud is null) return;
        _overlay?.Close();
        _model.Apply(action);
        _hud.Flash(_model.BuildMap());
    }

    private void ToggleMap()
    {
        if (_model is null || _overlay is null) return;
        if (_overlay.IsOpen) { _overlay.Close(); return; }

        // Configure from the day-to-day row so we never edit a scope we're standing inside.
        if (!_model.IsAtDayToDay) _model.Apply(NavAction.Surface);
        _overlay.Open(_model.BuildMap());
    }

    private void BuildTray()
    {
        var header = new NativeMenuItem("Hypertree 0.1.0") { IsEnabled = false };
        var map = new NativeMenuItem("Open map (Ctrl+Alt+Space)");
        map.Click += (_, _) => ToggleMap();
        var newScope = new NativeMenuItem("New scope here…");
        newScope.Click += (_, _) => { if (_model is not null) PromptNewScope(_model.CurrentColumnIndex); };
        var removeScope = new NativeMenuItem("Remove scope here");
        removeScope.Click += (_, _) => { if (_model is not null) RemoveScope(_model.CurrentColumnIndex); };
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu
            {
                header, new NativeMenuItemSeparator(),
                map, newScope, removeScope, new NativeMenuItemSeparator(),
                exit,
            },
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    // ── Scope definition (M1 stand-in for M2's git-worktree flow) ─────────────────

    // Prompt for and create a scope on the anchor at <index>. Reached from the tray (current column)
    // or the map overlay's "+ Add scope" (any column).
    private void PromptNewScope(int index)
    {
        if (_model is null || _desktops is null) return;
        if (_dialog is not null) { _dialog.Activate(); return; } // one at a time

        string anchorLabel = _model.BuildStreams()[index].AnchorLabel;
        _dialog = new ScopeDialog(anchorLabel);
        // Keep the dialog above the dimmed overlay if it's open.
        if (_overlay is { IsOpen: true }) _dialog.Topmost = true;
        _dialog.Closed += (_, _) => _dialog = null;
        _dialog.Confirmed += spec => CreateScope(index, spec);
        _dialog.Show();
    }

    private void CreateScope(int index, ScopeSpec spec)
    {
        if (_model is null || _desktops is null) return;

        // Provision one real desktop per label, named "<scope> · <label>", tracked as ours.
        var refs = new List<DesktopRef>(spec.Labels.Count);
        foreach (string label in spec.Labels)
        {
            DesktopId id = _desktops.Create($"{spec.Name} · {label}");
            _created.Add(id.Value);
            refs.Add(new DesktopRef(id, label));
        }

        HtScope? previous = _model.SetScope(index, new HtScope(spec.Name, refs));
        TearDown(previous, _model.AnchorDesktopId(index)); // remove a replaced scope's desktops
        AfterConfigChange();
    }

    private void RemoveScope(int index)
    {
        if (_model is null) return;
        TearDown(_model.SetScope(index, null), _model.AnchorDesktopId(index));
        AfterConfigChange();
    }

    // Refresh whichever surface is showing after a config change.
    private void AfterConfigChange()
    {
        if (_model is null) return;
        if (_overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap());
    }

    // Remove a scope's desktops — but ONLY ones Hypertree created, never the user's pre-existing
    // desktops (the demo topology reuses those). Windows on removed desktops fall back to <fallback>.
    private void TearDown(HtScope? scope, DesktopId fallback)
    {
        if (scope is null || _desktops is null) return;
        foreach (DesktopRef d in scope.Desktops)
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
