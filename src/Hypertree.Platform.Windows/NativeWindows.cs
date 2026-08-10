using System.Runtime.InteropServices;
using System.Text;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Shared top-level-window inspection primitives used by both <see cref="VirtualDesktopController"/> (to
/// count and list the app windows on each virtual desktop) and <see cref="WindowsWindowLayoutController"/>
/// (to capture and restore window placement).
/// </summary>
/// <remarks>
/// <see cref="IsCountableWindow"/> in particular MUST agree between the two callers — the invariant that
/// "the per-desktop window counts match what the layout capture sees" depends on both applying the exact
/// same filter. It used to be copied into each controller and kept in sync by a comment; living here once
/// makes that structural instead of aspirational.
/// </remarks>
internal static class NativeWindows
{
    private const int GWL_EXSTYLE = -20, GA_ROOTOWNER = 3;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// The alt-tab-ish filter: a visible, titled, top-level (un-owned) window that isn't a tool window,
    /// isn't one of our own (<paramref name="ownPid"/>), and isn't the shell's desktop/taskbar plumbing.
    /// Cloaked windows are kept — a window on another virtual desktop reads as "cloaked", and those are
    /// exactly what we're counting.
    /// </summary>
    public static bool IsCountableWindow(nint hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOTOWNER) != hwnd) return false;      // owned popup/dialog — skip
        if (GetWindowTextLength(hwnd) == 0) return false;               // untitled → not a real app window
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;                 // palettes/toolbars
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;                               // Hypertree's own windows
        return !IsShellWindow(hwnd);
    }

    public static bool IsShellWindow(nint hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                   or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow";
    }

    /// <summary>The window's title, or "" on failure — best-effort, advisory decoration only.</summary>
    public static string TitleOf(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The window's class name.</summary>
    public static string ClassOf(nint hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The owning process's name, or "" if the process is gone / access is denied (advisory only).</summary>
    public static string ProcessOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; } // process gone / access denied — advisory only
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, int flags);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(nint hwnd, StringBuilder buf, int max);
}
