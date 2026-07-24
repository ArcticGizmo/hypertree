using System.Text.Json;

namespace Hypertree.Settings;

/// <summary>
/// User-tunable settings, persisted to <c>%APPDATA%\hypertree\settings.json</c>. Kept behind
/// <see cref="ISettingsStore"/> so tests / non-Windows heads can swap in a fake. (Start-on-login is
/// NOT stored here — the OS registry is its source of truth; see <c>IStartupManager</c>.)
/// </summary>
public sealed class AppSettings
{
    /// <summary>When true the navigation flash stays up while Ctrl+Alt is held and fades a short beat
    /// after release; when false it shows for a fixed <see cref="FlashTimeoutMs"/> then hides.</summary>
    public bool FlashHoldToKeep { get; set; } = true;

    /// <summary>Grace after releasing Ctrl+Alt before the flash hides (hold-to-keep mode).</summary>
    public int FlashGraceMs { get; set; } = 100;

    /// <summary>Fixed on-screen time for the flash when hold-to-keep is off.</summary>
    public int FlashTimeoutMs { get; set; } = 1500;

    /// <summary>When true a persistent pill sits over the bottom of the primary screen naming the
    /// desktop you're on (prefixed with the group name, in the group's colour, when inside a group).
    /// It auto-hides while the cursor is near it so the taskbar underneath stays clickable.</summary>
    public bool ShowTaskbarLabel { get; set; } = true;

    /// <summary>Reusable group recipes, offered as a picker when standing up a new group so you don't
    /// retype the desktop set each time. Empty by default — you build them by promoting a group you
    /// already made ("Save current group as template…").</summary>
    public List<GroupTemplate> GroupTemplates { get; set; } = new();
}

/// <summary>
/// A reusable recipe for a group: a display <paramref name="Name"/> and its ordered desktop
/// <paramref name="Labels"/>. Picked when creating a new group to pre-fill the desktop set (the group's
/// own instance name is still typed per-branch — the template only carries the desktops).
/// </summary>
public sealed record GroupTemplate(string Name, IReadOnlyList<string> Labels);

/// <summary>Load/save the persisted settings. Behind an interface so tests use an in-memory fake.</summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>
/// Stores settings as JSON under <c>%APPDATA%\hypertree\settings.json</c>. All reads/writes are
/// best-effort: a missing or corrupt file yields defaults rather than throwing, so a bad file never
/// blocks startup (mirrors <c>FileStateStore</c>).
/// </summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileSettingsStore()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
