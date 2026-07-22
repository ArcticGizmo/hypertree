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

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree itself created (for scopes). Only these are ever torn down — the demo
    // topology maps onto the user's PRE-EXISTING desktops, which must never be removed.
    private readonly HashSet<Guid> _created = new();
    private IDesktopController? _desktops;
    private NavigationModel? _model;
    private HudWindow? _hud;
    private TrayIcon? _tray;
    private ScopeDialog? _dialog;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
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
    }

    // Apply the intent, then always flash the HUD map — so even a no-op at an edge still confirms
    // "where am I" (the load-bearing promise of PLAN.md §9 risk 3).
    private void Navigate(NavAction action)
    {
        if (_model is null || _hud is null) return;
        _model.Apply(action);
        _hud.Flash(_model.BuildMap());
    }

    private void BuildTray()
    {
        var header = new NativeMenuItem("Hypertree 0.1.0") { IsEnabled = false };
        var where = new NativeMenuItem("Show map");
        where.Click += (_, _) => { if (_model is not null) _hud?.Flash(_model.BuildMap()); };
        var newScope = new NativeMenuItem("New scope here…");
        newScope.Click += (_, _) => PromptNewScope();
        var removeScope = new NativeMenuItem("Remove scope here");
        removeScope.Click += (_, _) => RemoveScopeHere();
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
                where, newScope, removeScope, new NativeMenuItemSeparator(),
                exit,
            },
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    // ── Scope definition (M1 stand-in for M2's git-worktree flow) ─────────────────

    private void PromptNewScope()
    {
        if (_model is null || _desktops is null) return;
        if (!_model.IsAtDayToDay)
        {
            _hud?.Flash(_model.BuildMap()); // nudge: surface first
            return;
        }
        if (_dialog is not null) { _dialog.Activate(); return; } // one at a time

        _dialog = new ScopeDialog(_model.Location.DesktopLabel);
        _dialog.Closed += (_, _) => _dialog = null;
        _dialog.Confirmed += spec => CreateScope(spec);
        _dialog.Show();
    }

    private void CreateScope(ScopeSpec spec)
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

        HtScope? previous = _model.DefineScopeHere(new HtScope(spec.Name, refs));
        TearDown(previous); // remove the desktops of a replaced scope (only ones we created)
        _hud?.Flash(_model.BuildMap());
    }

    private void RemoveScopeHere()
    {
        if (_model is null || !_model.IsAtDayToDay || !_model.CurrentAnchorHasScope) return;
        TearDown(_model.RemoveScopeHere());
        _hud?.Flash(_model.BuildMap());
    }

    // Remove a scope's desktops — but ONLY ones Hypertree created, never the user's pre-existing
    // desktops (the demo topology reuses those). Fallback is the current anchor's desktop.
    private void TearDown(HtScope? scope)
    {
        if (scope is null || _model is null || _desktops is null) return;
        DesktopId fallback = _model.CurrentAnchorDesktopId;
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
        _hud?.Close();
    }
}
