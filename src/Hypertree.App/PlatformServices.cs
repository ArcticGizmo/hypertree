using Hypertree.Desktops;
using Hypertree.Launch;
using Hypertree.Platform;
#if WINDOWS
using Impl = Hypertree.Platform.Windows;
#endif

namespace Hypertree.App;

/// <summary>
/// Composition root for platform services: constructs the per-OS implementations of Core's platform
/// interfaces so no UI code references a concrete Win32/COM type directly (mirrors perch). Today only
/// the Windows set exists; the #if keeps the seam ready for a future head.
/// </summary>
internal static class PlatformServices
{
#if WINDOWS
    public static IDesktopController CreateDesktopController() => new Impl.VirtualDesktopController(new Impl.ForegroundActivator());
    public static IGlobalHotkey CreateGlobalHotkey() => new Impl.GlobalHotkey();
    public static IForegroundActivator CreateForegroundActivator() => new Impl.ForegroundActivator();
    public static IStartupManager CreateStartupManager() => new Impl.StartupManager();
    public static IPathInstaller CreatePathInstaller() => new Impl.PathInstaller();
    public static IAppCatalog CreateAppCatalog() => new Impl.ShellAppCatalog();
    public static IAppLauncher CreateAppLauncher() => new Impl.ShellAppLauncher();
    public static IAppIconProvider CreateAppIconProvider() => new Impl.ShellIconProvider();
#else
    public static IDesktopController CreateDesktopController()
        => throw new PlatformNotSupportedException("No desktop controller for this platform yet.");
    public static IGlobalHotkey CreateGlobalHotkey()
        => throw new PlatformNotSupportedException("No global hotkey for this platform yet.");
    public static IForegroundActivator CreateForegroundActivator()
        => throw new PlatformNotSupportedException("No foreground activator for this platform yet.");
    public static IStartupManager CreateStartupManager()
        => throw new PlatformNotSupportedException("No startup manager for this platform yet.");
    public static IPathInstaller CreatePathInstaller()
        => throw new PlatformNotSupportedException("No path installer for this platform yet.");
    public static IAppCatalog CreateAppCatalog()
        => throw new PlatformNotSupportedException("No app catalog for this platform yet.");
    public static IAppLauncher CreateAppLauncher()
        => throw new PlatformNotSupportedException("No app launcher for this platform yet.");
    public static IAppIconProvider CreateAppIconProvider()
        => throw new PlatformNotSupportedException("No app icon provider for this platform yet.");
#endif
}
