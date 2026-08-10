using System.Runtime.InteropServices;
using Hypertree.WindowLayout;

namespace Hypertree.Platform.Windows;

// Debug-only diagnostics: the traced restore and single-window probe behind the monitor-placement debug
// overlay. They perform (or inspect) the same placement the production Restore does, but return a per-window
// trace / snapshot instead of a bare count, so a mis-restore can be read off in the UI.
public sealed partial class WindowsWindowLayoutController
{
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
            string cls = NativeWindows.ClassOf(hwnd), proc = NativeWindows.ProcessOf(hwnd);
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
}
