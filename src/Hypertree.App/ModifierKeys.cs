using System.Runtime.InteropServices;
using Hypertree.Platform;

namespace Hypertree.App;

/// <summary>
/// Polls the physical modifier-key state via <c>GetAsyncKeyState</c>. A tray/hotkey process has no
/// focused window, so there are no key-up events to react to — the flash and the navigation gesture
/// both watch the modifiers directly to know when a Ctrl+Alt (or a rebound combo) is released.
/// </summary>
internal static class ModifierKeys
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12; // Shift, Ctrl, Alt
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool Down(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>True only when every modifier in <paramref name="mods"/> is currently held. An empty set
    /// returns false — there's nothing to hold — so callers fall back to a timed hide.</summary>
    public static bool ModifiersHeld(HotkeyModifiers mods)
    {
        if (mods == HotkeyModifiers.None) return false;
        if (mods.HasFlag(HotkeyModifiers.Control) && !Down(VK_CONTROL)) return false;
        if (mods.HasFlag(HotkeyModifiers.Alt)     && !Down(VK_MENU))    return false;
        if (mods.HasFlag(HotkeyModifiers.Shift)   && !Down(VK_SHIFT))   return false;
        if (mods.HasFlag(HotkeyModifiers.Win)     && !Down(VK_LWIN) && !Down(VK_RWIN)) return false;
        return true;
    }
}
