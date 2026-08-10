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
    public bool Launch(string target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
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
                return true;
            }

            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Hypertree.Diagnostics.Swallowed(ex, "ShellAppLauncher.Launch");
            return false; // nothing to fault the caller with — the launch just didn't happen
        }
    }
}
