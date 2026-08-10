using System.Runtime.InteropServices;

namespace Hypertree.Platform.Windows;

// The Win32 surface for WindowsWindowLayoutController — every P/Invoke, delegate, constant and struct the
// controller and its topology/diagnostics partials share. Kept in one place so the interop declarations (and
// the two silent-failure traps noted at their sites: the 64-byte DISPLAYCONFIG_MODE_INFO and the
// source/target device-info type constants) sit apart from the layout logic that calls them.
public sealed partial class WindowsWindowLayoutController
{
    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1, MDT_EFFECTIVE_DPI = 0;
    private const uint SW_MAXIMIZE = 3, SW_SHOWMINIMIZED = 2;
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
