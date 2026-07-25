using System.Runtime.InteropServices;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IForegroundActivator"/>. Windows blocks a process that isn't already the
/// foreground process from calling <c>SetForegroundWindow</c> outright, so we briefly attach our
/// input queue to the current foreground thread's — while attached the foreground restriction
/// doesn't apply — then set focus and detach. Used by the palette windows, which a global hotkey
/// summons and which must take typing immediately. (Pattern lifted from perch's WindowChrome.)
/// </summary>
public sealed class ForegroundActivator : IForegroundActivator
{
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public void ForceForeground(nint handle)
    {
        if (handle == 0) return;

        nint fg = GetForegroundWindow();
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            ShowWindow(handle, SW_SHOW);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }
}
