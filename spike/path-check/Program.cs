// Spike — THROWAWAY test instrument. Exercises the REAL PathInstaller against the REAL per-user PATH,
// because the one thing the unit tests can't cover is the registry round trip: whether the raw
// (unexpanded) value is read correctly and written back with its original value kind.
//
// It registers THIS spike's own output directory, not Hypertree's, so nothing about a real install is
// touched. It always removes it again, and verifies the value is byte-identical to what it started as —
// reporting loudly if it isn't, so a bad edit can't pass unnoticed.
//
// Exit code 0 = round trip clean, 1 = PATH was not restored exactly (go look at the backup).

using Hypertree.Platform.Windows;
using Microsoft.Win32;

const string EnvKey = "Environment";
const string ValueName = "Path";

static (string? raw, RegistryValueKind kind) Read()
{
    using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvKey);
    if (key is null) return (null, RegistryValueKind.Unknown);
    object? value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    return value is null ? (null, RegistryValueKind.Unknown) : (value as string, key.GetValueKind(ValueName));
}

var installer = new PathInstaller();
string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');

(string? before, RegistryValueKind kindBefore) = Read();
Console.WriteLine($"directory under test : {dir}");
Console.WriteLine($"PATH before          : {before?.Length ?? 0} chars, kind {kindBefore}");
Console.WriteLine($"already registered   : {installer.IsRegistered}");

installer.Register();
(string? during, RegistryValueKind kindDuring) = Read();
Console.WriteLine();
Console.WriteLine($"after Register()     : {during?.Length ?? 0} chars, kind {kindDuring}");
Console.WriteLine($"  contains our dir   : {installer.IsRegistered}");
Console.WriteLine($"  kind preserved     : {kindDuring == kindBefore}");
Console.WriteLine($"  appended exactly   : {during == (before is null or "" ? dir : before.TrimEnd(';') + ";" + dir)}");

// Registering again must be a no-op — every update calls it.
installer.Register();
(string? twice, _) = Read();
Console.WriteLine($"  idempotent         : {twice == during}");

installer.Unregister();
(string? after, RegistryValueKind kindAfter) = Read();
Console.WriteLine();
Console.WriteLine($"after Unregister()   : {after?.Length ?? 0} chars, kind {kindAfter}");
Console.WriteLine($"  contains our dir   : {installer.IsRegistered}");

// Unregistering again must be a no-op — it must not rewrite a PATH it has nothing to remove from.
installer.Unregister();
(string? twiceOff, _) = Read();

bool restored = after == before && kindAfter == kindBefore && twiceOff == after;
Console.WriteLine();
Console.WriteLine(restored
    ? "ROUND TRIP CLEAN — PATH is byte-identical to how it started."
    : "MISMATCH — PATH was NOT restored exactly. Restore from your backup.");

if (!restored)
{
    Console.WriteLine($"  before: {before}");
    Console.WriteLine($"  after : {after}");
}

return restored ? 0 : 1;
