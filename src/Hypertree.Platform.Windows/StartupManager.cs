using Microsoft.Win32;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IStartupManager"/> via the per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Enabling writes the running
/// executable's path under the value <c>Hypertree</c>; disabling deletes it. Per-user (HKCU) needs no
/// elevation. All operations are best-effort — a registry hiccup must never crash the tray.
/// </summary>
/// <remarks>
/// Only a real (Velopack) install may write the Run key. A copy run from the build tree
/// (<c>bin\Debug</c>, <c>publish\</c>) that wrote its own path here would hijack login away from the
/// installed copy, and — because it isn't Velopack-installed — then report itself a "dev build" when
/// asked to update. That is exactly the autostart poisoning this gate prevents. The installed copy also
/// re-asserts its path on every launch (App startup calls <see cref="SetEnabled"/> when already enabled),
/// so a Run entry left pointing at a stale location self-heals the next time the real install runs.
/// </remarks>
public sealed class StartupManager : IStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hypertree";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex) { Diagnostics.Swallowed(ex, "StartupManager.IsEnabled"); return false; }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                // A build-tree copy must never advertise itself for autostart (see remarks).
                if (!IsInstalledBuild()) return;

                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                // Idempotent: rewrite only when the stored path is missing or stale, so re-asserting on
                // every launch to self-heal a stale entry doesn't churn the registry.
                string want = $"\"{exe}\"";
                if (key.GetValue(ValueName) as string != want) key.SetValue(ValueName, want);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { Diagnostics.Swallowed(ex, "StartupManager.SetEnabled"); }
    }

    // A Velopack install lays the app out as  <root>\current\hypertree.exe  with a sibling
    // <root>\Update.exe; a build-tree copy has no such Update.exe two levels up. This is the same install
    // shape Velopack's own IsInstalled looks for, checked here on disk so the platform layer needs no
    // Velopack dependency of its own.
    private static bool IsInstalledBuild()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            string? current = exe is null ? null : Path.GetDirectoryName(exe);
            string? root = current is null ? null : Path.GetDirectoryName(current);
            return root is not null && File.Exists(Path.Combine(root, "Update.exe"));
        }
        catch { return false; }
    }
}
