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

    /// <summary>When true, a <b>dive or surface</b> chord (the vertical, branch-changing moves) pressed
    /// while the flash is <i>not</i> on screen only raises the flash — it shows you where you are without
    /// moving. The next press (still holding the modifiers, so the flash is still up) navigates for real.
    /// Left/right moves within a row are unaffected: they always move immediately, since you stay on a row
    /// you can already see. Off means every press moves immediately. On by default — the disorientation is
    /// in the vertical jump, where you land among a fresh set of lookalike desktops. See <c>App.RevealOnly</c>.</summary>
    public bool DisplayBeforeMoving { get; set; } = true;

    /// <summary>When true, a navigation move slides the flash board in from the direction of travel
    /// (left/right within a row, up/down for dive/surface) instead of snapping it in place — a directional
    /// cue that echoes the traditional desktop-switch slide. The real desktop still switches instantly
    /// underneath (the OS owns that); this is Hypertree's own overlay carrying the motion. On by default,
    /// but always yields to the Windows "Show animations" system setting — with animations off there, no
    /// slide plays regardless of this flag. See <c>App.Navigate</c> and <c>HudWindow.Flash</c>.</summary>
    public bool AnimateNavigation { get; set; } = true;

    /// <summary>Which edge the navigation wipe starts on. True (default) begins the dark band on the edge you
    /// moved <i>toward</i> — the leading edge — and sweeps it away across the screen; false begins it on the
    /// opposite edge and sweeps toward where you're heading. Purely a taste knob; only has an effect while
    /// <see cref="AnimateNavigation"/> is on. See <c>HudWindow.SweepTravel</c>.</summary>
    public bool SweepFromLeadingEdge { get; set; } = true;

    /// <summary>How the board is drawn wherever it appears — the flash, the map, card backdrops, previews,
    /// the move flow. <see cref="MapStyle.Board"/> (default) is the screen-tile layout; <see cref="MapStyle.Metro"/>
    /// is the transit-diagram "metro map". A whole-app appearance choice, set in Settings → Appearance or by
    /// pressing <c>v</c> on the map.</summary>
    public MapStyle MapStyle { get; set; } = MapStyle.Board;

    /// <summary>Reusable branch recipes, offered via the branch card's "Load from template" button so you
    /// don't retype the desktop set each time. Empty by default — you build and delete them in the
    /// "Manage templates…" command.</summary>
    public List<BranchTemplate> BranchTemplates { get; set; } = new();

    /// <summary>User overrides for the global hotkeys. Only commands the user has rebound are stored;
    /// everything else resolves to <see cref="Hotkeys.Defaults"/>. See <see cref="ResolveHotkeys"/>.</summary>
    public List<HotkeyBinding> HotkeyBindings { get; set; } = new();

    /// <summary>When true, the first launch after the version changes pops a "what's new" window listing
    /// only the changelog entries newer than <see cref="LastSeenVersion"/>. On by default; the window's
    /// "Don't show changelogs again" button (and the Settings toggle) flip it off. See <c>ChangelogWindow</c>
    /// and <c>App.Startup</c>.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>The app version that last ran on this machine, stamped every launch. Compared against the
    /// running version at startup to detect an update and pick which changelog entries are new. Null on a
    /// fresh install (nothing to show — seeded silently on first run). See the startup changelog check.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>The effective chord for every command: the built-in defaults overlaid with the stored
    /// overrides. This is what the composition root registers with the OS.</summary>
    public IReadOnlyDictionary<HotkeyCommand, HotkeyChord> ResolveHotkeys()
    {
        var map = new Dictionary<HotkeyCommand, HotkeyChord>(Hotkeys.Defaults);
        foreach (HotkeyBinding b in HotkeyBindings) map[b.Command] = new HotkeyChord(b.Modifiers, b.Key);
        return map;
    }
}

/// <summary>How the board is rendered across the app. Persisted as a string in settings.json (via the
/// enum converter), so the names are load-bearing — don't rename without a migration.</summary>
public enum MapStyle
{
    /// <summary>The screen-tile board: desktops as little screen mockups in rows.</summary>
    Board,

    /// <summary>The transit-diagram metro map: timelines as coloured lines, desktops as stations.</summary>
    Metro,
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
        : this(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree"))
    {
    }

    /// <summary>Testing seam: keep <c>settings.json</c> in an explicit directory instead of the roaming
    /// profile, so a round-trip can be exercised without touching a real install's settings.</summary>
    internal FileSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            // Read with the SAME options used to write (crucially the string-enum converter) — otherwise a
            // string-serialised enum like "MapStyle": "Metro" can't be parsed back, the whole load throws,
            // and every setting silently reverts to its default.
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
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
