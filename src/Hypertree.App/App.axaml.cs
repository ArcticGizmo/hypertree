using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;

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
    private IDesktopController? _desktops;
    private NavigationModel? _model;
    private HudWindow? _hud;
    private TrayIcon? _tray;

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
        _model.Changed += () => { /* HUD flash happens in the hotkey handler so edges show too */ };

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

    // Apply the intent, then always flash the HUD with the current location — so even a no-op at an
    // edge still confirms "where am I" (the load-bearing promise of PLAN.md §9 risk 3).
    private void Navigate(NavAction action)
    {
        if (_model is null || _hud is null) return;
        _model.Apply(action);
        _hud.Flash(_model.Location.Format());
    }

    private void BuildTray()
    {
        var header = new NativeMenuItem("Hypertree 0.1.0") { IsEnabled = false };
        var where = new NativeMenuItem("Where am I");
        where.Click += (_, _) => { if (_model is not null) _hud?.Flash(_model.Location.Format()); };
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu { header, new NativeMenuItemSeparator(), where, new NativeMenuItemSeparator(), exit },
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void Teardown()
    {
        foreach (var hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
        if (_tray is not null) _tray.IsVisible = false;
        _hud?.Close();
    }
}
