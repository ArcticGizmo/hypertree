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
///
/// A <c>record</c> so a partial edit can be expressed as <c>original with { ChangedField = … }</c> —
/// carrying every other field (including ones a given surface doesn't edit) by construction, instead of
/// hand-copying pass-through fields and silently resetting any that were forgotten.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Where the persistent desktop-name pill sits (or <see cref="LabelPlacement.Off"/> to hide
    /// it). The pill names the desktop you're on — prefixed with the branch name, in the branch's colour,
    /// when inside a branch — docked to the chosen corner/edge of the primary screen, and auto-hides while
    /// the cursor is near it so whatever's underneath stays clickable. Superseded the old on/off
    /// <c>ShowTaskbarLabel</c> bool; see <see cref="FileSettingsStore.Load"/> for how old files migrate.</summary>
    public LabelPlacement TaskbarLabelPlacement { get; set; } = LabelPlacement.Off;

    /// <summary>When true a floating, draggable panel (default top-right) lists every row of the stack in
    /// map order — main and each branch — with the desktop a click would land on, so you can jump between
    /// branches with the mouse. It collapses to a lone logo bubble (its header, or the toggle-switcher
    /// hotkey). Off by default: the mouse switcher is the exception, not the norm. See <c>SwitcherWindow</c>.</summary>
    public bool ShowSwitcher { get; set; } = false;

    /// <summary>Whether the switcher is showing as the collapsed logo bubble (true) or the full list
    /// (false). Persisted so it reopens the way you left it.</summary>
    public bool SwitcherCollapsed { get; set; }

    /// <summary>The switcher's top-left position in physical pixels while <b>expanded</b>, or null to dock it
    /// top-right of the primary screen. Set once you drag it, so it stays put across restarts. X and Y move
    /// together — both null (docked) or both set (explicit). The collapsed bubble keeps its own separate
    /// position (<see cref="SwitcherCollapsedX"/>), so each state can live where it suits you.</summary>
    public int? SwitcherX { get; set; }
    public int? SwitcherY { get; set; }

    /// <summary>The switcher's top-left position in physical pixels while <b>collapsed</b> to the bubble, or
    /// null to dock it top-right. Kept apart from the expanded position so dragging the bubble doesn't move
    /// the full panel and vice versa.</summary>
    public int? SwitcherCollapsedX { get; set; }
    public int? SwitcherCollapsedY { get; set; }

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
    /// the move flow. <see cref="MapStyle.Ascii"/> (default) is the monospace terminal look;
    /// <see cref="MapStyle.Board"/> is the screen-tile layout and <see cref="MapStyle.Metro"/> the
    /// transit-diagram "metro map". A whole-app appearance choice, set in Settings → Appearance or by
    /// pressing <c>v</c> on the map.</summary>
    public MapStyle MapStyle { get; set; } = MapStyle.Ascii;

    /// <summary>The spatial map's zoom factor, set by pressing <c>+</c> / <c>-</c> (and <c>0</c> to reset) on
    /// the map. 1.0 is the default 100%; the map clamps it to a legible range when applied. Persisted so the
    /// map reopens at the zoom you left it. See <c>SpatialOverlay</c>.</summary>
    public double MapZoom { get; set; } = 1.0;

    /// <summary>Whether the spatial map's key legend (the help panel in the top-left) is shown. Toggled with
    /// <c>l</c> on the map and persisted, so power users can hide it for good — it covers a fair slice of the
    /// screen and isn't needed once the keys are second nature. Defaults on for discoverability. See
    /// <c>SpatialOverlay</c>.</summary>
    public bool ShowMapLegend { get; set; } = true;

    /// <summary>The window-picker's zoom factor — the size of the live window thumbnails in the "move" and
    /// "pull" flows, set by pressing <c>Ctrl</c>+<c>+</c> / <c>Ctrl</c>+<c>-</c> in the picker. 1.0 is the
    /// default 100%; the picker clamps it to a legible range when applied. Persisted so the picker reopens at
    /// the size you left it — near-identical windows are easier to tell apart when scaled up. See
    /// <c>WindowPickerContent</c>.</summary>
    public double PickerZoom { get; set; } = 1.0;

    /// <summary>Reusable branch recipes, offered via the branch card's "Load from template" button so you
    /// don't retype the desktop set each time. Empty by default — you build and delete them in the
    /// "Manage templates…" command.</summary>
    public List<BranchTemplate> BranchTemplates { get; set; } = new();

    /// <summary>User overrides for the global hotkeys. Only commands the user has rebound are stored;
    /// everything else resolves to <see cref="Hotkeys.Defaults"/>. See <see cref="ResolveHotkeys"/>.</summary>
    public List<HotkeyBinding> HotkeyBindings { get; set; } = new();

    /// <summary>User-defined launcher entries: a named target (an app, file, folder or URL) plus optional
    /// arguments and working directory, all shell-executed. Surfaced in the application launcher
    /// (Ctrl+Alt+O) above the discovered apps, and set up in its "Manage custom commands…" screen. Empty by
    /// default. See <c>CustomCommand</c>.</summary>
    public List<CustomCommand> CustomCommands { get; set; } = new();

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

/// <summary>Where the desktop-name pill docks on the primary screen — a corner or an edge center — with
/// <see cref="Off"/> hiding it entirely. <see cref="Off"/> is deliberately first (value 0) so it's the
/// default. Persisted as a string in settings.json (via the enum converter), so the names are
/// load-bearing — don't rename without a migration.</summary>
public enum LabelPlacement
{
    /// <summary>The pill is hidden. The default, and the first option in the settings dropdown.</summary>
    Off,
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>How the board is rendered across the app. Persisted as a string in settings.json (via the
/// enum converter), so the names are load-bearing — don't rename without a migration.</summary>
public enum MapStyle
{
    /// <summary>The screen-tile board: desktops as little screen mockups in rows.</summary>
    Board,

    /// <summary>The transit-diagram metro map: timelines as coloured lines, desktops as stations.</summary>
    Metro,

    /// <summary>The terminal look: desktops as monospace box-drawing cards joined by an ASCII spine.</summary>
    Ascii,
}

/// <summary>
/// A reusable recipe for a branch: a display <paramref name="Name"/> and its ordered desktop
/// <paramref name="Labels"/>. Picked when creating a new branch to pre-fill the desktop set (the branch's
/// own instance name is still typed per-branch — the template only carries the desktops).
/// </summary>
public sealed record BranchTemplate(string Name, IReadOnlyList<string> Labels);

/// <summary>
/// A user-defined launcher entry. <paramref name="Name"/> is what you type to find it; the rest is handed
/// to the shell, exactly as if you'd double-clicked it: <paramref name="Target"/> is the app / file /
/// folder / URL to open, with optional <paramref name="Arguments"/> and <paramref name="WorkingDirectory"/>.
/// Persisted in <c>settings.json</c> (see <see cref="AppSettings.CustomCommands"/>); the two optional
/// fields are null when left blank.
/// </summary>
public sealed record CustomCommand(string Name, string Target,
                                   string? Arguments = null, string? WorkingDirectory = null);

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

    public FileSettingsStore() : this(Hypertree.Store.StateDirectory.Path) { }

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
            string json = File.ReadAllText(Path);
            // Read with the SAME options used to write (crucially the string-enum converter) — otherwise a
            // string-serialised enum like "MapStyle": "Metro" can't be parsed back, the whole load throws,
            // and every setting silently reverts to its default.
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            MigrateLegacyTaskbarLabel(json, settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    // Pre-placement builds stored the desktop label as an on/off bool ("ShowTaskbarLabel"); it's now the
    // "TaskbarLabelPlacement" enum. When an old file has the bool but not the new key, carry the choice
    // across — on → its old fixed spot (bottom center), off → hidden. A current file has the enum and no
    // bool, so this is a no-op. (The stray "ShowTaskbarLabel" is simply ignored on load and drops out the
    // next time settings are saved.)
    private static void MigrateLegacyTaskbarLabel(string json, AppSettings settings)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("TaskbarLabelPlacement", out _)) return;              // already on the new key
        if (!root.TryGetProperty("ShowTaskbarLabel", out JsonElement legacy)) return; // nothing to migrate
        settings.TaskbarLabelPlacement =
            legacy.ValueKind == JsonValueKind.True ? LabelPlacement.BottomCenter : LabelPlacement.Off;
    }

    public void Save(AppSettings settings)
    {
        try { Hypertree.Store.StateDirectory.WriteAtomic(Path, JsonSerializer.Serialize(settings, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
