using System.Runtime.InteropServices;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IForegroundActivator"/>. Activating a window from a process that isn't the
/// foreground one is the whole problem: a bare <c>SetForegroundWindow</c> from a background process is
/// not activated but <b>demoted to a taskbar "needs attention" flash</b> (and it still returns TRUE, so
/// the caller can't tell). During a desktop switch that flash lands on windows that aren't even on the
/// desktop you moved to — the reported cross-desktop flashing (see
/// <c>docs/design/taskbar-attention-flash-on-switch.md</c>).
///
/// The strategy is two-stage:
/// <list type="number">
///   <item>Try a plain activation first. When the caller already holds foreground standing — the tray
///   does on the hotkey path (it received <c>WM_HOTKEY</c>) and the overlay-click path — this activates
///   cleanly with no side effects, and, crucially, without the input-queue churn that itself can provoke
///   flashing.</item>
///   <item>Only if that didn't land, fall back to the canonical force-foreground technique: neutralise
///   the foreground-lock timeout for the duration of the call (and borrow the foreground thread's input
///   state) so the activation is granted instead of flashed, then restore. This covers the standing-less
///   path (a switch driven by <c>htree goto</c>, where a separate CLI process asked the tray to switch).</item>
/// </list>
/// Used by the desktop-switch handover and by the palette windows, which a global hotkey summons and
/// which must take typing immediately.
/// </summary>
public sealed class ForegroundActivator : IForegroundActivator
{
    private const int SW_SHOW = 5;
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // Two shapes of the same import: SPI_GET* writes the value through a pointer (ref), SPI_SET* passes
    // the new value *in* pvParam itself (as an integer cast to the pointer slot).
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, ref uint pvParam, uint fWinIni);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, nint pvParam, uint fWinIni);

    public void ForceForeground(nint handle)
    {
        if (handle == 0) return;

        ShowWindow(handle, SW_SHOW);
        BringWindowToTop(handle);

        // Stage 1: plain activation. Lands when we already hold foreground standing (hotkey / overlay).
        SetForegroundWindow(handle);
        SetFocus(handle);
        if (GetForegroundWindow() == handle) return;

        // Stage 2: it didn't take — we lack standing (e.g. the switch came in over the control pipe from
        // htree). Force it: zero the foreground-lock timeout so SetForegroundWindow is granted rather than
        // flashed, and attach to the current foreground thread's input as a second lever. Both are undone
        // in the finally, so the system setting is neutralised only for the length of this call.
        uint thisThread = GetCurrentThreadId();
        nint fg = GetForegroundWindow();
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);

        uint savedTimeout = 0;
        bool timeoutZeroed = SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref savedTimeout, 0)
                             && SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, 0, 0);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            if (timeoutZeroed) SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (nint)savedTimeout, 0);
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }
}
