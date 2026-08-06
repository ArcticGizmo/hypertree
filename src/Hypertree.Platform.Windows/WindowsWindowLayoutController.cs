using System.Runtime.InteropServices;
using System.Text;
using Hypertree.Platform;
using Hypertree.WindowLayout;

namespace Hypertree.Platform.Windows;

/// <summary>
/// The Win32 implementation of <see cref="IWindowLayoutController"/> — the monitor-layout axis's counterpart
/// to <see cref="VirtualDesktopController"/>. Enumerates monitors with EDID-derived stable ids
/// (<c>QueryDisplayConfig</c>), captures window placement (<c>GetWindowPlacement</c>), and restores it
/// (<c>SetWindowPlacement</c>). The proving ground for every call here is <c>spike/monitor-layout/</c>; the
/// two silent-failure traps it surfaced (the 64-byte <c>DISPLAYCONFIG_MODE_INFO</c>, and the
/// source/target device-info type constants) are noted at their sites.
/// </summary>
/// <remarks>
/// The process must be Per-Monitor-V2 DPI aware (declared in <c>app.manifest</c>) or the coordinates and
/// per-monitor DPI read here are virtualised and wrong on mixed-DPI rigs. The window filter is
/// <see cref="IsCountableWindow"/>, kept identical to <see cref="VirtualDesktopController"/>'s so a layout
/// captures exactly the "real app windows" the map counts — no more.
/// </remarks>
public sealed class WindowsWindowLayoutController : IWindowLayoutController
{
    // Used to put the foreground back after a restore: the show commands that maximize a window activate it,
    // and there's no no-activate variant of maximize, so a restore would otherwise leave focus on whichever
    // window it maximized last (e.g. Slack), yanking the user to that monitor.
    private readonly IForegroundActivator _foreground;

    public WindowsWindowLayoutController(IForegroundActivator foreground) => _foreground = foreground;

    public IReadOnlyList<MonitorRef> Monitors() => EnumMonitors().Select(x => x.mon).ToList();

    public MonitorLayoutSnapshot Snapshot()
    {
        List<(MonitorRef mon, string gdi)> mons = EnumMonitors();
        var byGdi = new Dictionary<string, MonitorRef>(StringComparer.OrdinalIgnoreCase);
        foreach ((MonitorRef m, string gdi) in mons) byGdi[gdi] = m;

        var windows = new List<WindowPlacement>();
        uint own = GetCurrentProcessId();
        EnumWindows((hwnd, _) =>
        {
            if (!IsCountableWindow(hwnd, own)) return true;
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return true;

            // which monitor: HMONITOR -> gdi name -> our stable id + bounds
            nint hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            string gdi = GdiNameOf(hmon);
            byGdi.TryGetValue(gdi, out MonitorRef? m);
            int monLeft = m?.Bounds.Left ?? 0, monTop = m?.Bounds.Top ?? 0;

            // Store the normal rect as an offset from this window's monitor origin, so restore re-anchors it
            // to wherever that monitor lands next dock; a same-monitor round-trip cancels exactly.
            var offset = new Recti(
                wp.rcNormalPosition.left - monLeft, wp.rcNormalPosition.top - monTop,
                wp.rcNormalPosition.right - wp.rcNormalPosition.left,
                wp.rcNormalPosition.bottom - wp.rcNormalPosition.top);

            windows.Add(new WindowPlacement((long)hwnd, m?.StableId ?? "", TitleOf(hwnd), offset, ShowOf(wp.showCmd)));
            return true;
        }, 0);

        List<MonitorRef> refs = mons.Select(x => x.mon).ToList();
        return new MonitorLayoutSnapshot(MonitorSet.Key(refs), refs, windows);
    }

    public RestoreReport Restore(MonitorLayoutSnapshot snapshot)
    {
        List<(MonitorRef mon, string gdi)> mons = EnumMonitors();
        var byStable = new Dictionary<string, MonitorRef>(StringComparer.OrdinalIgnoreCase);
        var gdiToStable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((MonitorRef m, string gdi) in mons) { byStable[m.StableId] = m; gdiToStable[gdi] = m.StableId; }

        nint priorForeground = GetForegroundWindow();                               // keep the user where they were

        int placed = 0, gone = 0, noMon = 0, refused = 0;
        foreach (WindowPlacement w in snapshot.Windows)
        {
            nint hwnd = (nint)w.Hwnd;
            if (!IsWindow(hwnd)) { gone++; continue; }                              // same-session HWND no longer valid
            if (!byStable.TryGetValue(w.MonitorStableId, out MonitorRef? mon)) { noMon++; continue; } // that screen isn't present

            if (ApplyPlacement(hwnd, mon, w, CurrentStableId(hwnd, gdiToStable))) placed++; else refused++;
        }

        // Maximizing a window activates it (no no-activate maximize exists), so hand the foreground back to
        // wherever it was before the restore rather than leaving it on the last window we maximized.
        if (priorForeground != 0 && IsWindow(priorForeground)) _foreground.ForceForeground(priorForeground);
        return new RestoreReport(placed, gone, noMon, refused);
    }

    // Put one window at its target rect + show-state on the destination monitor. The subtlety this exists
    // for: SetWindowPlacement with SW_MAXIMIZE CANNOT move an already-maximized window to another monitor —
    // it keeps it maximized where it is and only stashes rcNormalPosition. So when a maximized window must
    // change monitors, restore it onto the destination as a normal window first, then re-maximize there.
    // (Only when the monitor actually changes, to avoid a restore/maximize flicker on windows already in place.)
    private static bool ApplyPlacement(nint hwnd, MonitorRef mon, WindowPlacement w, string currentStableId)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return false;

        int left = mon.Bounds.Left + w.NormalOffset.Left;                            // re-anchor the offset onto the target monitor
        int top = mon.Bounds.Top + w.NormalOffset.Top;
        var target = new RECT { left = left, top = top, right = left + w.NormalOffset.Width, bottom = top + w.NormalOffset.Height };
        wp.rcNormalPosition = target;

        bool crossMonitor = !string.Equals(currentStableId, w.MonitorStableId, StringComparison.OrdinalIgnoreCase);
        if (w.Show == ShowState.Maximized && crossMonitor)
        {
            // 1) relocate onto the destination as a normal window (this is the move SW_MAXIMIZE won't do),
            //    without activating…
            wp.showCmd = SW_SHOWNOACTIVATE;
            SetWindowPlacement(hwnd, ref wp);
            // 2) …then maximize, which now maximizes on the destination monitor the window sits on. This one
            //    activates (no no-activate maximize exists); Restore hands the foreground back afterwards.
            wp.rcNormalPosition = target; // preserved as the restore rect
            wp.showCmd = SW_MAXIMIZE;
            return SetWindowPlacement(hwnd, ref wp);
        }

        // Prefer the no-activate show commands so restoring a whole layout doesn't churn focus window by
        // window; only same-monitor maximize (which is usually a no-op reassert) still activates.
        wp.showCmd = w.Show switch
        {
            ShowState.Maximized => SW_MAXIMIZE,
            ShowState.Minimized => SW_SHOWMINNOACTIVE,
            _ => SW_SHOWNOACTIVATE,
        };
        return SetWindowPlacement(hwnd, ref wp);                                      // elevated / UWP may refuse — best-effort
    }

    // The stable id of the monitor a window currently sits on ("" if unknown) — to decide whether a restore
    // is a cross-monitor move.
    private static string CurrentStableId(nint hwnd, Dictionary<string, string> gdiToStable)
        => gdiToStable.TryGetValue(GdiNameOf(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)), out string? s) ? s : "";

    // ── Diagnostics (debug only) ──────────────────────────────────────────────────────────────────────
    public IReadOnlyList<WindowRestoreTrace> RestoreTraced(MonitorLayoutSnapshot snapshot)
    {
        List<(MonitorRef mon, string gdi)> mons = EnumMonitors();
        var byStable = new Dictionary<string, MonitorRef>(StringComparer.OrdinalIgnoreCase);
        var gdiToStable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((MonitorRef m, string gdi) in mons) { byStable[m.StableId] = m; gdiToStable[gdi] = m.StableId; }

        var rows = new List<WindowRestoreTrace>();
        foreach (WindowPlacement w in snapshot.Windows)
        {
            nint hwnd = (nint)w.Hwnd;
            byStable.TryGetValue(w.MonitorStableId, out MonitorRef? mon);
            string wantName = mon?.Friendly ?? w.MonitorStableId;
            string cls = ClassOf(hwnd), proc = ProcessOf(hwnd);
            Recti before = RectOf(hwnd);

            if (!IsWindow(hwnd))
            {
                rows.Add(new WindowRestoreTrace(w.Hwnd, w.Title, proc, cls, wantName, mon is not null, w.Show,
                    before, default, before, false, 0, "gone (HWND no longer valid)"));
                continue;
            }
            if (mon is null)
            {
                rows.Add(new WindowRestoreTrace(w.Hwnd, w.Title, proc, cls, wantName, false, w.Show,
                    before, default, before, false, 0, "monitor-missing (target screen not present)"));
                continue;
            }

            string currentStable = CurrentStableId(hwnd, gdiToStable);
            bool crossMonitor = !string.Equals(currentStable, w.MonitorStableId, StringComparison.OrdinalIgnoreCase);
            int left = mon.Bounds.Left + w.NormalOffset.Left, top = mon.Bounds.Top + w.NormalOffset.Top;
            var targetRect = new Recti(left, top, w.NormalOffset.Width, w.NormalOffset.Height);

            bool ok = ApplyPlacement(hwnd, mon, w, currentStable);
            int err = Marshal.GetLastWin32Error();
            Recti after = RectOf(hwnd);
            string how = w.Show == ShowState.Maximized && crossMonitor ? "placed (maximized cross-monitor: restore→maximize)"
                       : ok ? "placed" : "refused (SetWindowPlacement returned false)";
            rows.Add(new WindowRestoreTrace(w.Hwnd, w.Title, proc, cls, wantName, true, w.Show,
                before, targetRect, after, ok, err, how));
        }
        return rows;
    }

    public WindowProbe Probe(long hwnd)
    {
        nint h = (nint)hwnd;
        if (!IsWindow(h)) return new WindowProbe(false, default, "", "", ShowState.Normal);

        Recti rect = RectOf(h);
        string gdi = GdiNameOf(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST));
        MonitorRef? mon = null;
        foreach ((MonitorRef m, string g) in EnumMonitors())
            if (string.Equals(g, gdi, StringComparison.OrdinalIgnoreCase)) { mon = m; break; }

        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        ShowState show = GetWindowPlacement(h, ref wp) ? ShowOf(wp.showCmd) : ShowState.Normal;
        return new WindowProbe(true, rect, mon?.StableId ?? "", mon?.Friendly ?? "", show);
    }

    private static Recti RectOf(nint hwnd)
        => GetWindowRect(hwnd, out RECT r) ? new Recti(r.left, r.top, r.right - r.left, r.bottom - r.top) : default;

    private static string ClassOf(nint hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcessOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    // ── Monitor enumeration + the stable-id chain ─────────────────────────────────────────────────────
    // Each MonitorRef is paired with its GDI name (\\.\DISPLAYn) — internal only, used to attribute a
    // window (via MonitorFromWindow) to a monitor. The GDI name shuffles across dock cycles, so it never
    // leaves this file; the OS-free MonitorRef carries only the stable id.
    private static List<(MonitorRef mon, string gdi)> EnumMonitors()
    {
        Dictionary<string, (string path, string friendly)> stable = BuildStableIdMap();
        var list = new List<(MonitorRef, string)>();
        EnumDisplayMonitors(0, 0, (hmon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hmon, ref mi)) return true;
            string gdi = mi.szDevice;
            uint dpi = 96;
            if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0) dpi = dpiX;
            (string path, string friendly) = stable.TryGetValue(gdi, out var s) ? s : (gdi, gdi);
            var mon = new MonitorRef(
                path,
                string.IsNullOrEmpty(friendly) ? gdi : friendly,
                new Recti(mi.rcMonitor.left, mi.rcMonitor.top,
                          mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top),
                (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, dpi);
            list.Add((mon, gdi));
            return true;
        }, 0);
        return list;
    }

    // active display path -> (stable EDID device path, friendly name), keyed by the shuffling GDI name.
    private static Dictionary<string, (string, string)> BuildStableIdMap()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode) != 0) return map;
        var paths = new DISPLAYCONFIG_PATH_INFO[nPath];
        var modes = new DISPLAYCONFIG_MODE_INFO[nMode];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, 0) != 0) return map;

        for (int i = 0; i < nPath; i++)
        {
            DISPLAYCONFIG_PATH_INFO p = paths[i];

            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId, id = p.targetInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

            string gdi = src.viewGdiDeviceName;        // "\\.\DISPLAY1"  (shuffles across dock cycles)
            string path = tgt.monitorDevicePath;       // EDID-derived    (stable)
            if (!string.IsNullOrEmpty(gdi) && !string.IsNullOrEmpty(path))
                map[gdi] = (path, tgt.monitorFriendlyDeviceName ?? "");
        }
        return map;
    }

    private static string GdiNameOf(nint hmon)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(hmon, ref mi) ? mi.szDevice : "";
    }

    // ── Window filter — kept identical to VirtualDesktopController.IsCountableWindow ──────────────────
    private static bool IsCountableWindow(nint hwnd, uint ownPid)
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

    private static bool IsShellWindow(nint hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                   or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow";
    }

    private static string TitleOf(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static ShowState ShowOf(uint showCmd) => showCmd switch
    {
        SW_MAXIMIZE => ShowState.Maximized,
        SW_SHOWMINIMIZED => ShowState.Minimized,
        _ => ShowState.Normal
    };

    // ── P/Invoke ──────────────────────────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20, GA_ROOTOWNER = 3;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1, MDT_EFFECTIVE_DPI = 0;
    private const uint SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_SHOWMINIMIZED = 2;
    private const uint SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    // Device-info request types: SOURCE_NAME=1, TARGET_NAME=2. Swapping them mismatches the struct size and
    // every DisplayConfigGetDeviceInfo returns ERROR_INVALID_PARAMETER (proven in the spike).
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);
    private delegate bool MonitorEnumProc(nint hmon, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint p);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, int flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(nint h, StringBuilder b, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint h, StringBuilder b, int max);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint h, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint h, uint type, out uint x, out uint y);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint nPath, out uint nMode);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint nPath, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint nMode, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME req);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME req);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public uint length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO { public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling; public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    // The union is 48 bytes (its largest member), making the whole struct 64 — the size QueryDisplayConfig
    // checks. We never read the mode payload, only size it. Get this wrong and QueryDisplayConfig returns
    // ERROR_INVALID_PARAMETER and the stable-id chain silently falls back to the shuffling GDI name.
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] payload; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
}
