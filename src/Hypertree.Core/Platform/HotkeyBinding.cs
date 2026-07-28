namespace Hypertree.Platform;

/// <summary>
/// The rebindable global commands. Each maps to one chord (see <see cref="HotkeyChord"/>); the four
/// navigation commands share the Ctrl+Alt+Arrow default layer, and the palette / move commands sit on
/// the same modifier by default. Kept in Core so both the settings UI and the composition root agree on
/// the set and its order.
/// </summary>
public enum HotkeyCommand
{
    Dive,
    Surface,
    MoveLeft,
    MoveRight,
    Peek,
    CommandPalette,
    OpenMap,
    // Kept for backward compatibility: move-windows no longer has a default chord (it's reached via "m" on
    // the map), but a user who deliberately rebound it still has that override in settings.json, so the
    // member must stay for those bindings to deserialize and register. See Hotkeys.Defaults / ActionFor.
    MoveWindows,
}

/// <summary>A modifier combination plus a trigger key — the shape a global hotkey registers.</summary>
public sealed record HotkeyChord(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    /// <summary>A human-readable rendering, e.g. <c>Ctrl+Alt+Down</c>.</summary>
    public string Display()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Hotkeys.KeyDisplay(Key));
        return string.Join("+", parts);
    }
}

/// <summary>
/// A persisted override of one command's chord (stored in settings; see <c>AppSettings.HotkeyBindings</c>).
/// Only commands the user has changed are stored — everything else falls back to <see cref="Hotkeys.Defaults"/>.
/// </summary>
public sealed record HotkeyBinding(HotkeyCommand Command, HotkeyModifiers Modifiers, HotkeyKey Key);

/// <summary>Static knowledge about the hotkey commands: their order, display names, defaults and key rendering.</summary>
public static class Hotkeys
{
    /// <summary>The commands in the order the settings window lists them (navigation first, then actions).</summary>
    public static readonly IReadOnlyList<HotkeyCommand> Order = new[]
    {
        HotkeyCommand.Dive, HotkeyCommand.Surface, HotkeyCommand.MoveLeft, HotkeyCommand.MoveRight,
        HotkeyCommand.Peek, HotkeyCommand.CommandPalette, HotkeyCommand.OpenMap,
    };

    /// <summary>The out-of-the-box chords. Ctrl+Alt+Arrow is the nav layer (M0: Win+Ctrl+Arrow is the
    /// native switch); the palette and open-map commands sit on Ctrl+Alt+P / Ctrl+Alt+M. Move-windows has
    /// no default chord (reached via "m" on the map), so it isn't listed here — see <see cref="HotkeyCommand.MoveWindows"/>.</summary>
    public static readonly IReadOnlyDictionary<HotkeyCommand, HotkeyChord> Defaults =
        new Dictionary<HotkeyCommand, HotkeyChord>
        {
            [HotkeyCommand.Dive]           = new(NavMods, HotkeyKey.ArrowDown),
            [HotkeyCommand.Surface]        = new(NavMods, HotkeyKey.ArrowUp),
            [HotkeyCommand.MoveLeft]       = new(NavMods, HotkeyKey.ArrowLeft),
            [HotkeyCommand.MoveRight]      = new(NavMods, HotkeyKey.ArrowRight),
            [HotkeyCommand.Peek]           = new(NavMods, HotkeyKey.Space),
            [HotkeyCommand.CommandPalette] = new(NavMods, HotkeyKey.P),
            [HotkeyCommand.OpenMap]        = new(NavMods, HotkeyKey.M),
        };

    private const HotkeyModifiers NavMods = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    public static string DisplayName(HotkeyCommand command) => command switch
    {
        HotkeyCommand.Dive           => "Dive (down)",
        HotkeyCommand.Surface        => "Surface (up)",
        HotkeyCommand.MoveLeft       => "Move left",
        HotkeyCommand.MoveRight      => "Move right",
        HotkeyCommand.Peek           => "Peek at the board",
        HotkeyCommand.CommandPalette => "Command palette",
        HotkeyCommand.OpenMap        => "Open map",
        HotkeyCommand.MoveWindows    => "Move windows",
        _ => command.ToString(),
    };

    /// <summary>Render a trigger key for display: arrows as "Up"/"Down"/…, digits as "0".."9", the rest verbatim.</summary>
    public static string KeyDisplay(HotkeyKey key) => key switch
    {
        HotkeyKey.ArrowUp    => "Up",
        HotkeyKey.ArrowDown  => "Down",
        HotkeyKey.ArrowLeft  => "Left",
        HotkeyKey.ArrowRight => "Right",
        HotkeyKey.Space      => "Space",
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => ((int)(key - HotkeyKey.D0)).ToString(),
        _ => key.ToString(), // letters (A–Z) and function keys (F1–F12) render as their member name
    };
}
