using System.Runtime.InteropServices;
using Hypertree.Platform;
using Microsoft.Win32;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IPathInstaller"/>: keeps Hypertree's install directory on the <b>per-user</b> PATH,
/// so both <c>htree</c> and <c>hypertree</c> resolve in any terminal. Per-user needs no elevation.
/// Driven from the Velopack install/update/uninstall hooks; already-open shells keep their inherited
/// environment and need restarting to see the change.
/// </summary>
/// <remarks>
/// <para><b>Why the registry rather than <c>Environment.SetEnvironmentVariable</c>.</b> The convenient API
/// reads PATH <em>expanded</em> and writes it back as a plain string. If the user's PATH contains
/// <c>%JAVA_HOME%\bin</c> — and plenty do — a round trip through it silently bakes in today's value of
/// that variable, permanently. The entry keeps working until the variable changes, then breaks somewhere
/// far from here. Reading the raw value and preserving its
/// <see cref="RegistryValueKind">kind</see> is the only way to edit user PATH without that side effect.</para>
///
/// <para>Everything is best-effort: a failure to register leaves the commands reachable by full path,
/// which is a far better outcome than a failed install.</para>
/// </remarks>
public sealed class PathInstaller : IPathInstaller
{
    private const string EnvironmentKey = "Environment";
    private const string ValueName = "Path";

    /// <summary>The directory to register — where the running executable lives. During a Velopack hook
    /// that is the newly-installed <c>current\</c> folder, which holds both binaries and keeps its path
    /// across updates.</summary>
    private static string InstallDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    public bool IsRegistered
    {
        get
        {
            try { return PathEntries.Contains(ReadRaw(out _), InstallDir()); }
            catch (Exception ex) { Diagnostics.Swallowed(ex, "PathInstaller.IsRegistered"); return false; }
        }
    }

    public void Register() => Edit(current => PathEntries.Add(current, InstallDir()));

    public void Unregister() => Edit(current => PathEntries.Remove(current, InstallDir()));

    // Read, transform, write back only if the transform actually changed something — so an update that
    // re-registers an unchanged path, or an uninstall of something never registered, touches nothing.
    private static void Edit(Func<string?, string?> transform)
    {
        try
        {
            string? current = ReadRaw(out RegistryValueKind kind);
            if (transform(current) is not { } updated) return;

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);
            key.SetValue(ValueName, updated, KindFor(kind, updated));
            Broadcast();
        }
        // best-effort — never fail an install over a PATH entry
        catch (Exception ex) { Diagnostics.Swallowed(ex, "PathInstaller.Edit"); }
    }

    // The raw, UNEXPANDED value. Absent PATH reads as null, which the pure helpers already handle.
    private static string? ReadRaw(out RegistryValueKind kind)
    {
        kind = RegistryValueKind.ExpandString;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        if (key is null) return null;

        object? value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;

        kind = key.GetValueKind(ValueName);
        return value as string;
    }

    // Keep whatever kind the value already had, so we don't demote a REG_EXPAND_SZ PATH to REG_SZ and
    // freeze everyone else's variables. If the result contains a variable reference it must be expandable
    // regardless of what was there before.
    private static RegistryValueKind KindFor(RegistryValueKind existing, string updated)
        => existing == RegistryValueKind.ExpandString || updated.Contains('%')
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;

    // Tell the shell the environment changed, so newly-launched terminals pick up the new PATH without a
    // logoff. Timed out and best-effort: a hung listener must not stall an installer.
    private const int HwndBroadcast = 0xFFFF;
    private const int WmSettingChange = 0x1A;
    private const int SmtoAbortIfHung = 0x2;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern nint SendMessageTimeout(
        nint hWnd, int msg, nint wParam, string lParam, int flags, int timeoutMs, out nint result);

    private static void Broadcast()
    {
        try { SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _); }
        catch { /* nothing to do if the broadcast fails — a new shell still gets it */ }
    }
}
