using System.Runtime.InteropServices;
using Hypertree.WindowLayout;

namespace Hypertree.Platform.Windows;

// Monitor enumeration and the stable-id chain. Each MonitorRef is paired with its GDI name (\\.\DISPLAYn) —
// internal only, used to attribute a window (via MonitorFromWindow) to a monitor. The GDI name shuffles
// across dock cycles, so it never leaves this file; the OS-free MonitorRef carries only the EDID-derived
// stable id.
public sealed partial class WindowsWindowLayoutController
{
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
}
