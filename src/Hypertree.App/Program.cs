using Avalonia;
using Velopack;

namespace Hypertree.App;

internal static class Program
{
    // Avalonia's classic desktop lifetime. STA because the app drives shell COM (the virtual-desktop
    // interop) on this thread, and the tray/windows need it.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack install/update lifecycle hook — must run before anything else. No-op unless
        // launched with the special --veloapp-* hook args (i.e. during install/update).
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
