using System.Diagnostics;
using Hypertree.Launch;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IAppLauncher"/>: starts a target through the shell, the way double-clicking it in
/// Explorer would. <c>UseShellExecute</c> is what lets one code path open an <c>.exe</c>, a Start-menu
/// <c>.lnk</c>, a document, a folder, or a <c>http(s)</c>/<c>mailto</c> URL — the shell picks the handler.
/// Every failure (bad path, a declined UAC prompt) is caught and reported as a false, so a mistyped custom
/// command can never take the tray down.
/// </summary>
public sealed class ShellAppLauncher : IAppLauncher
{
    public LaunchResult Launch(string target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return LaunchResult.Failed;
        try
        {
            // A packaged app is a "shell:AppsFolder\<AUMID>" moniker, not a file — hand it to Explorer, which
            // resolves the moniker and activates the app. ArgumentList quotes it cleanly (some AUMIDs have
            // spaces). ShellExecute doesn't launch these reliably, so this path is deliberate, not a fallback.
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                explorer.ArgumentList.Add(target);
                Process.Start(explorer);
                // Explorer is only the activation broker: the packaged app it starts is not its child, and
                // explorer's own pid owns unrelated windows. Report "started, pid unknown" so window matching
                // falls back to the executable name rather than trusting a misleading pid.
                return LaunchResult.Ok(null);
            }

            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            // The pid drives window attribution during a loadout restore; it's null when the shell reused an
            // already-running process (single-instance apps), which the restore then handles by name.
            Process? p = Process.Start(psi);
            return LaunchResult.Ok(p?.Id);
        }
        catch
        {
            return LaunchResult.Failed; // nothing to fault the caller with — the launch just didn't happen
        }
    }
}
