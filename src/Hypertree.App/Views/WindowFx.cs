using System.Runtime.InteropServices;

namespace Hypertree.App.Views;

/// <summary>Small DWM tweaks for the app's overlay windows.</summary>
internal static class WindowFx
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Turn off the DWM open/close/show animation for a window, so a summoned overlay snaps in
    /// rather than scaling/fading up from the corner. Idempotent and best-effort.</summary>
    public static void DisableTransitions(nint hwnd)
    {
        if (hwnd == 0) return;
        int on = 1; // TRUE = transitions force-disabled
        try { DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int)); }
        catch { /* best-effort — losing the tweak just restores the default animation */ }
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int pvParam, uint winIni);

    /// <summary>Whether the user has Windows animations turned on (Settings → Accessibility → Visual
    /// effects → Animation effects). This is the OS "reduce motion" signal: when it's off we suppress
    /// Hypertree's own navigation slide so the app honours the system-wide preference. Best-effort —
    /// if the query fails we assume animations are allowed rather than silently killing the effect.</summary>
    public static bool SystemAnimationsEnabled()
    {
        int enabled = 1;
        try
        {
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0)) return enabled != 0;
        }
        catch { /* fall through to the permissive default */ }
        return true;
    }
}
