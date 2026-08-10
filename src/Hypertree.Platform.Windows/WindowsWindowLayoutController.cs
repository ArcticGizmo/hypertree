using System.Runtime.InteropServices;
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
/// <para>The process must be Per-Monitor-V2 DPI aware (declared in <c>app.manifest</c>) or the coordinates and
/// per-monitor DPI read here are virtualised and wrong on mixed-DPI rigs. The window filter is
/// <see cref="NativeWindows.IsCountableWindow"/>, shared with <see cref="VirtualDesktopController"/> so a
/// layout captures exactly the "real app windows" the map counts — no more.</para>
///
/// <para>Split across partials by concern: this file is the production capture/restore path;
/// <c>.Topology</c> owns monitor enumeration and the stable-id chain, <c>.Diagnostics</c> the debug-only
/// traced restore / probe, and <c>.Interop</c> the shared P/Invoke, structs and constants.</para>
/// </remarks>
public sealed partial class WindowsWindowLayoutController : IWindowLayoutController
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
            if (!NativeWindows.IsCountableWindow(hwnd, own)) return true;
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

            windows.Add(new WindowPlacement((long)hwnd, m?.StableId ?? "", NativeWindows.TitleOf(hwnd), offset, ShowOf(wp.showCmd)));
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

    // The countable-window filter, TitleOf / ProcessOf / ClassOf live in NativeWindows — shared with
    // VirtualDesktopController so the two apply the exact same window filter (see NativeWindows).
    private static ShowState ShowOf(uint showCmd) => showCmd switch
    {
        SW_MAXIMIZE => ShowState.Maximized,
        SW_SHOWMINIMIZED => ShowState.Minimized,
        _ => ShowState.Normal
    };
}
