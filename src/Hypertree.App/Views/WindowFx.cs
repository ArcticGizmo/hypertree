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

    // ── Overlay window styling (topmost band · click-through · tool-window) ─────────────────────────────
    // The extended-style flags and SetWindowPos plumbing that the app's several borderless overlay windows
    // (the flash, the taskbar label, the switcher, the dim/host stage, the restore curtain) all need. These
    // MUST agree — an overlay that gets the wrong ex-style either steals focus or stops taking clicks — so
    // the declarations live here once instead of being copied into each window class.
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)] private static extern long GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern long SetWindowLongPtr(nint hWnd, int nIndex, long dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // OR extended-style bits onto a window (whatever it already has). No-op on a null handle.
    private static void AddExStyle(nint hwnd, long bits)
    {
        if (hwnd == 0) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | bits);
    }

    /// <summary>Make a window click-through and non-focus-stealing, and keep it out of the taskbar/alt-tab
    /// (TRANSPARENT | LAYERED | NOACTIVATE | TOOLWINDOW) — the passive-overlay style (the flash, the taskbar
    /// label).</summary>
    public static void SetClickThrough(nint hwnd)
        => AddExStyle(hwnd, WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    /// <summary>Keep a window off the desktop and non-activating but NOT click-through (LAYERED | NOACTIVATE
    /// | TOOLWINDOW) — for an opaque overlay that must not fight the real foreground (the restore curtain).</summary>
    public static void SetNoActivate(nint hwnd)
        => AddExStyle(hwnd, WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    /// <summary>Keep a window out of the taskbar/alt-tab (TOOLWINDOW) only — without making it click-through
    /// or non-activating, for an overlay that must still take clicks (the switcher).</summary>
    public static void SetToolWindow(nint hwnd)
        => AddExStyle(hwnd, WS_EX_TOOLWINDOW);

    /// <summary>Re-assert a window at the top of the always-on-top band without moving, sizing, or
    /// activating it — so it keeps its no-focus-steal contract while re-lifting after a desktop switch.</summary>
    public static void LiftTopmost(nint hwnd)
    {
        if (hwnd == 0) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
