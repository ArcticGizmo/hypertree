using Microsoft.Win32;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IStartupManager"/> via the per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Enabling writes the running
/// executable's path under the value <c>Hypertree</c>; disabling deletes it. Per-user (HKCU) needs no
/// elevation. All operations are best-effort — a registry hiccup must never crash the tray.
/// </summary>
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
            catch { return false; }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort */ }
    }
}
