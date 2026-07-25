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
}
