using System.Runtime.InteropServices;
using Hypertree.Launch;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IAppCatalog"/>: the installed apps Windows Search lists, from its two sources —
/// the Start-menu shortcut trees (classic Win32 apps) <b>and</b> the shell "AppsFolder" (packaged /
/// Store / UWP apps, which never drop a <c>.lnk</c>: Windows Terminal, Calculator, WhatsApp, …). Both feed
/// the OS-free <see cref="AppCatalogFilter"/>, which dedupes and sorts. Shortcuts are scanned first, so a
/// Win32 app that appears in both keeps its <c>.lnk</c> (a launch target the shell resolves directly, with
/// a clean icon) rather than its AppsFolder twin.
/// </summary>
public sealed class ShellAppCatalog : IAppCatalog
{
    // The two Start-menu shortcut roots, all-users first, then the current user's own.
    private static readonly string[] ShortcutRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    };

    public IReadOnlyList<AppEntry> Discover()
    {
        var candidates = new List<(string Name, string Path)>();
        candidates.AddRange(StartMenuShortcuts());
        candidates.AddRange(AppsFolderItems()); // packaged apps — the ones with no shortcut
        return AppCatalogFilter.FromShortcuts(candidates);
    }

    private static IEnumerable<(string Name, string Path)> StartMenuShortcuts()
    {
        var found = new List<(string, string)>();
        foreach (string root in ShortcutRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue; // an unreadable subtree shouldn't sink the whole scan
            }
            foreach (string file in files)
                found.Add((Path.GetFileNameWithoutExtension(file), file));
        }
        return found;
    }

    // Enumerate the AppsFolder via the Shell automation object — the same namespace ("shell:AppsFolder")
    // Windows Search draws packaged apps from. Each item's Path is its AppUserModelID (e.g.
    // "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"); we store the launch target as the shell moniker
    // "shell:AppsFolder\<AUMID>", which ShellExecute/Explorer resolves and which the icon provider keys on.
    private static IEnumerable<(string Name, string Path)> AppsFolderItems()
    {
        var found = new List<(string, string)>();
        object? shell = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return found;
            shell = Activator.CreateInstance(shellType);
            dynamic app = shell!;
            dynamic folder = app.NameSpace("shell:AppsFolder");
            if (folder is null) return found;

            // Cast the COM FolderItems collection to IEnumerable rather than foreach-ing a dynamic — the COM
            // enumerator is exposed through IEnumerable, and this is the reliable way to walk it.
            var items = (System.Collections.IEnumerable)folder.Items();
            foreach (object obj in items)
            {
                dynamic item = obj;
                string name = item.Name as string ?? "";
                string path = item.Path as string ?? "";
                if (name.Length == 0 || path.Length == 0) continue;
                // Legacy AppsFolder entries can carry a real file path; packaged ones carry a bare AUMID.
                bool isMoniker = !path.Contains(@":\") && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
                found.Add((name, isMoniker ? $"shell:AppsFolder\\{path}" : path));
            }
        }
        catch
        {
            // COM unavailable / a shell that won't enumerate — fall back to just the shortcut apps.
        }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
        return found;
    }
}
