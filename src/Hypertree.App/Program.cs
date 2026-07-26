using Avalonia;
using Velopack;

namespace Hypertree.App;

internal static class Program
{
    /// <summary>
    /// This process's single-instance claim, once it has won one. Null in <c>--shot</c> mode (which is
    /// exempt from the guard). <see cref="App"/> hangs its "another launch asked us to surface" handling
    /// off this.
    /// </summary>
    internal static SingleInstance? Instance { get; private set; }

    // Avalonia's classic desktop lifetime. STA because the app drives shell COM (the virtual-desktop
    // interop) on this thread, and the tray/windows need it.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack install/update lifecycle hook — must run before anything else. No-op unless
        // launched with the special --veloapp-* hook args (i.e. during install/update).
        VelopackApp.Build().Run();

        // The design-shot harness (tools/, --shot) spins up a throwaway copy purely to render captures and
        // shuts itself down again. It's not a second Hypertree in any meaningful sense, so it neither claims
        // the slot nor gets turned away by one already held by the tray copy.
        if (Array.IndexOf(args, "--shot") >= 0)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        // One Hypertree per session. If a copy is already in the tray it's been asked to surface its command
        // palette (see App.Startup) and this launch is done — starting a rival would mean two tray icons,
        // half the hotkeys refused by the OS, and two writers on the same desktop state.
        using SingleInstance? instance = SingleInstance.Claim();
        if (instance is null) return;
        Instance = instance;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
