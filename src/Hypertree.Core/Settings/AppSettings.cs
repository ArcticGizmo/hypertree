using System.Text.Json;
using System.Text.Json.Serialization;
using Hypertree.Platform;

namespace Hypertree.Settings;

/// <summary>
/// User-tunable settings, persisted to <c>%APPDATA%\hypertree\settings.json</c>. Kept behind
/// <see cref="ISettingsStore"/> so tests / non-Windows heads can swap in a fake. (Start-on-login is
/// NOT stored here — the OS registry is its source of truth; see <c>IStartupManager</c>.)
///
/// The navigation flash is no longer configurable — its hold-to-keep behaviour and timings are fixed
/// constants in <c>HudWindow</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>When true a persistent pill sits over the bottom of the primary screen naming the
    /// desktop you're on (prefixed with the branch name, in the branch's colour, when inside a branch).
    /// It auto-hides while the cursor is near it so the taskbar underneath stays clickable.</summary>
    public bool ShowTaskbarLabel { get; set; } = true;

    /// <summary>Reusable branch recipes, offered via the branch card's "Load from template" button so you
    /// don't retype the desktop set each time. Empty by default — you build and delete them in the
    /// "Manage templates…" command.</summary>
    public List<BranchTemplate> BranchTemplates { get; set; } = new();

    /// <summary>User overrides for the global hotkeys. Only commands the user has rebound are stored;
    /// everything else resolves to <see cref="Hotkeys.Defaults"/>. See <see cref="ResolveHotkeys"/>.</summary>
    public List<HotkeyBinding> HotkeyBindings { get; set; } = new();

    /// <summary>The effective chord for every command: the built-in defaults overlaid with the stored
    /// overrides. This is what the composition root registers with the OS.</summary>
    public IReadOnlyDictionary<HotkeyCommand, HotkeyChord> ResolveHotkeys()
    {
        var map = new Dictionary<HotkeyCommand, HotkeyChord>(Hotkeys.Defaults);
        foreach (HotkeyBinding b in HotkeyBindings) map[b.Command] = new HotkeyChord(b.Modifiers, b.Key);
        return map;
    }
}

/// <summary>
/// A reusable recipe for a branch: a display <paramref name="Name"/> and its ordered desktop
/// <paramref name="Labels"/>. Picked when creating a new branch to pre-fill the desktop set (the branch's
/// own instance name is still typed per-branch — the template only carries the desktops).
/// </summary>
public sealed record BranchTemplate(string Name, IReadOnlyList<string> Labels);

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
    // String-based enum serialization keeps hotkey bindings readable in settings.json (e.g.
    // "Control, Alt" / "ArrowDown" rather than opaque integers).
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

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
