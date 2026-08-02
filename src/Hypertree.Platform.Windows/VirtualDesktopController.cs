using System.Runtime.InteropServices;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IDesktopController"/> — drives virtual desktops through the ImmersiveShell's
/// undocumented <see cref="IVirtualDesktopManagerInternal"/> (and <see cref="IApplicationViewCollection"/>
/// for moving foreign windows). All the build-fragile interop is in <see cref="ComInterop"/>-defined
/// interfaces; this class is just the mapping to Core's clean API. Proven on build 26200 (M0).
///
/// COM RCWs are apartment-bound: construct and use this on the app's single UI (STA) thread. Hotkey
/// callbacks marshal to that thread before calling in.
/// </summary>
public sealed class VirtualDesktopController : IDesktopController
{
    private readonly IVirtualDesktopManagerInternal _vdm;
    private readonly IApplicationViewCollection _views;
    private readonly IVirtualDesktopPinnedApps _pinned;
    private readonly IVirtualDesktopManager _publicVdm; // documented API — window→desktop lookup only
    private readonly IForegroundActivator _foreground;  // hands foreground to a destination window on switch

    public VirtualDesktopController(IForegroundActivator foreground)
    {
        _foreground = foreground;

        Type shellType = Type.GetTypeFromCLSID(Guids.CLSID_ImmersiveShell)
                         ?? throw new PlatformNotSupportedException("ImmersiveShell CLSID unavailable.");
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;

        Guid svc = Guids.CLSID_VirtualDesktopManagerInternal;
        Guid iid = typeof(IVirtualDesktopManagerInternal).GUID;
        _vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iid);

        Guid avc = typeof(IApplicationViewCollection).GUID;
        _views = (IApplicationViewCollection)shell.QueryService(ref avc, ref avc);

        Guid pin = Guids.CLSID_VirtualDesktopPinnedApps;
        Guid pinIid = typeof(IVirtualDesktopPinnedApps).GUID;
        _pinned = (IVirtualDesktopPinnedApps)shell.QueryService(ref pin, ref pinIid);

        Type vdmType = Type.GetTypeFromCLSID(Guids.CLSID_VirtualDesktopManager)
                       ?? throw new PlatformNotSupportedException("VirtualDesktopManager CLSID unavailable.");
        _publicVdm = (IVirtualDesktopManager)Activator.CreateInstance(vdmType)!;
    }

    public int Count => _vdm.GetCount();

    public DesktopId Current => new(_vdm.GetCurrentDesktop().GetId());

    public IReadOnlyList<DesktopInfo> List()
    {
        _vdm.GetDesktops(out IObjectArray arr);
        arr.GetCount(out int n);
        Guid iid = typeof(IVirtualDesktop).GUID;
        var result = new List<DesktopInfo>(n);
        for (int i = 0; i < n; i++)
        {
            arr.GetAt(i, ref iid, out object o);
            var vd = (IVirtualDesktop)o;
            result.Add(new DesktopInfo(new DesktopId(vd.GetId()), HString.Read(vd.GetName()), i));
        }
        return result;
    }

    // Count "real" application windows per desktop by walking every top-level window and asking the
    // documented API which desktop it belongs to. Best-effort: any window we can't classify is skipped
    // rather than allowed to throw — the counts are advisory decoration on the map.
    public IReadOnlyDictionary<DesktopId, int> WindowCounts()
    {
        var counts = new Dictionary<DesktopId, int>();
        foreach ((_, Guid g) in EnumAppWindows())
        {
            var id = new DesktopId(g);
            counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
        }
        return counts;
    }

    // The same countable-window walk, but returning each window's handle + title + owning process for
    // the desktop asked about — the "move windows" picker. Text is best-effort (empty on failure).
    public IReadOnlyList<WindowInfo> WindowsOn(DesktopId id)
    {
        List<nint> monitors = MonitorHandles(); // computed once per call; windows map their HMONITOR to an index
        var result = new List<WindowInfo>();
        foreach ((nint hwnd, Guid g) in EnumAppWindows())
            if (g == id.Value)
                result.Add(new WindowInfo(hwnd, TitleOf(hwnd), ProcessOf(hwnd), PathOf(hwnd), MonitorIndexOf(hwnd, monitors)));
        return result;
    }

    // Every app window across all desktops (not just one), each with its path — the snapshot session
    // restore diffs before/after a launch to find the window that launch produced.
    public IReadOnlyList<WindowInfo> AllWindows()
    {
        var result = new List<WindowInfo>();
        foreach ((nint hwnd, Guid _) in EnumAppWindows())
            result.Add(new WindowInfo(hwnd, TitleOf(hwnd), ProcessOf(hwnd), PathOf(hwnd)));
        return result;
    }

    public DesktopId? DesktopOf(nint hwnd)
    {
        if (_publicVdm.GetWindowDesktopId(hwnd, out Guid g) != 0 || g == Guid.Empty) return null; // HR != S_OK / unassigned
        return new DesktopId(g);
    }

    public void CloseWindow(nint hwnd)
    {
        if (hwnd != 0) PostMessage(hwnd, WM_CLOSE, 0, 0); // graceful; the window owns whether it honours it
    }

    // The window's monitor as a 1-based index into the shared monitor enumeration (0 if it can't be
    // resolved). Same enumeration order MoveWindowToMonitor uses, so a captured index means the same screen.
    private static int MonitorIndexOf(nint hwnd, List<nint> monitors)
    {
        nint hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        int i = monitors.IndexOf(hmon);
        return i >= 0 ? i + 1 : 0;
    }

    private static List<nint> MonitorHandles()
    {
        var list = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT _, nint _) => { list.Add(h); return true; }, 0);
        return list;
    }

    // Put a window on the given monitor (1-based). Best-effort "on the right screen" placement, not exact
    // geometry: keep the window's size (clamped to the target's work area) and drop it at the work-area
    // top-left; a maximised window is restored, moved, then re-maximised so it fills the destination screen.
    public void MoveWindowToMonitor(nint hwnd, int monitor)
    {
        if (hwnd == 0 || monitor < 1) return;
        List<nint> monitors = MonitorHandles();
        if (monitor > monitors.Count) return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitors[monitor - 1], ref mi)) return;
        RECT work = mi.rcWork;

        bool zoomed = IsZoomed(hwnd);
        if (zoomed) ShowWindow(hwnd, SW_RESTORE);
        if (GetWindowRect(hwnd, out RECT r))
        {
            int w = Math.Min(r.right - r.left, work.right - work.left);
            int h = Math.Min(r.bottom - r.top, work.bottom - work.top);
            SetWindowPos(hwnd, 0, work.left, work.top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        if (zoomed) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    // Walk every top-level window once, keeping only the "real" app windows (IsCountableWindow) that
    // the documented API attributes to a concrete desktop. Shared by WindowCounts and WindowsOn.
    private List<(nint hwnd, Guid desktop)> EnumAppWindows()
    {
        var list = new List<(nint, Guid)>();
        uint own = GetCurrentProcessId();
        EnumWindows((hwnd, _) =>
        {
            if (!IsCountableWindow(hwnd, own)) return true;
            if (_publicVdm.GetWindowDesktopId(hwnd, out Guid g) != 0) return true; // HR != S_OK
            if (g == Guid.Empty) return true; // pinned / all-desktops / unassigned — don't attribute to one
            list.Add((hwnd, g));
            return true;
        }, 0);
        return list;
    }

    private static string TitleOf(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcessOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; } // process gone / access denied — advisory only
    }

    // The full executable path behind a window, for session capture (the key we relaunch by).
    // QueryFullProcessImageName under PROCESS_QUERY_LIMITED_INFORMATION reads across integrity levels
    // where the managed Process.MainModule would throw. Best-effort: a process we can't open (gone,
    // protected) yields "" — the window is simply not capturable and drops out of the session.
    private static string PathOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int cap = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : "";
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    // The alt-tab-ish filter: a visible, titled, top-level (un-owned) window that isn't a tool window,
    // isn't one of our own, and isn't the shell's desktop/taskbar plumbing. Cloaked windows are kept —
    // a window on another virtual desktop reads as "cloaked", and those are exactly what we're counting.
    private static bool IsCountableWindow(nint hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOTOWNER) != hwnd) return false;      // owned popup/dialog — skip
        if (GetWindowTextLength(hwnd) == 0) return false;               // untitled → not a real app window
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;                 // palettes/toolbars
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;                               // Hypertree's own map/palette
        return !IsShellWindow(hwnd);
    }

    private static bool IsShellWindow(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                   or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow";
    }

    private const int GWL_EXSTYLE = -20, GA_ROOTOWNER = 3;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lparam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, int flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder buf, int max);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, System.Text.StringBuilder buf, ref int size);
    private const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    // Monitor placement (recipe restore). One shared EnumDisplayMonitors ordering keys both capture and place.
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_RESTORE = 9, SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint hmon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);

    // Switch/rename/remove tolerate a desktop that no longer exists (e.g. the user deleted it from
    // Task View): the id is stale, so there's nothing to do — no-op rather than crash the tray. The
    // navigation model reconciles the stale record separately.
    public void SwitchTo(DesktopId id)
    {
        if (TryResolve(id) is not { } vd) return;
        _vdm.SwitchDesktop(vd);
        HandoffForeground();
    }

    // A bare SwitchDesktop moves the desktop but not the foreground window. If the window that was
    // foreground lived on the desktop we left, it stays foreground — now DWM-cloaked on another desktop —
    // and once cloaked, no process can move the foreground off it (proven externally; see
    // docs/design/foreground-handover-on-switch.md). Every later "focus my window" call is then dead until
    // the user clicks something, and keystrokes go to an invisible window. The shell's own switcher
    // (Win+Ctrl+Arrow) avoids this by activating a window on the destination; do the same, inline, so the
    // stranded state never outlives the switch.
    private void HandoffForeground()
    {
        nint fg = GetForegroundWindow();
        if (fg != 0 && _publicVdm.IsWindowOnCurrentVirtualDesktop(fg, out int onCurrent) == 0 && onCurrent != 0)
            return; // the foreground already belongs to the desktop we switched to — nothing is stranded

        // Prefer the top-most ordinary window on the destination; fall back to the shell (desktop) window,
        // which clears the anomaly on an empty desktop rather than leaving focus on the cloaked window.
        nint target = TopWindowOnCurrentDesktop();
        if (target == 0) target = GetShellWindow();
        if (target != 0) _foreground.ForceForeground(target);
    }

    // The top-most (Z-order-first) ordinary, non-minimised window on the desktop now shown — EnumWindows
    // yields front-to-back, so the first match wins. Same filter as the window counts, narrowed to the
    // current desktop; 0 when the destination has no such window (e.g. an empty desktop, or one holding
    // only minimised windows — activating those would un-minimise them, which the shell switcher doesn't).
    private nint TopWindowOnCurrentDesktop()
    {
        Guid current = _vdm.GetCurrentDesktop().GetId();
        uint own = GetCurrentProcessId();
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCountableWindow(hwnd, own) || IsIconic(hwnd)) return true;
            if (_publicVdm.GetWindowDesktopId(hwnd, out Guid g) != 0 || g != current) return true;
            found = hwnd;
            return false; // stop at the first (top-most) match
        }, 0);
        return found;
    }

    public DesktopId Create(string name)
    {
        IVirtualDesktop vd = _vdm.CreateDesktop();
        SetName(vd, name);
        return new DesktopId(vd.GetId());
    }

    public void Rename(DesktopId id, string name)
    {
        if (TryResolve(id) is { } vd) SetName(vd, name);
    }

    // MoveDesktop is the one call whose index convention we can't read off a spec, so we don't trust it
    // blindly: after the move we re-read the desktop's ordinal and, if the shell landed it somewhere other
    // than asked, correct once by the same offset. A second miss is left alone — the desktop is still on
    // the timeline, just not in the requested slot.
    public void Reorder(DesktopId id, int index)
    {
        IVirtualDesktop? vd = TryResolve(id);
        if (vd is null) return;                 // already gone
        int n = _vdm.GetCount();
        if (n <= 1) return;
        index = Math.Clamp(index, 0, n - 1);
        try
        {
            _vdm.MoveDesktop(vd, index);
            int landed = OrdinalOf(id);
            if (landed >= 0 && landed != index)
                _vdm.MoveDesktop(vd, Math.Clamp(index + (index - landed), 0, n - 1));
        }
        catch (COMException) { /* shell refused the reorder — the desktop keeps its place */ }
    }

    private int OrdinalOf(DesktopId id)
    {
        IReadOnlyList<DesktopInfo> all = List();
        for (int i = 0; i < all.Count; i++) if (all[i].Id == id) return i;
        return -1;
    }

    public void Remove(DesktopId id, DesktopId fallback)
    {
        IVirtualDesktop? vd = TryResolve(id);
        if (vd is null) return;                 // already gone
        IVirtualDesktop? fb = TryResolve(fallback) ?? _vdm.GetCurrentDesktop();
        if (fb is not null) _vdm.RemoveDesktop(vd, fb);
    }

    public string GetName(DesktopId id) => TryResolve(id) is { } vd ? HString.Read(vd.GetName()) : "";

    public void MoveWindowToDesktop(nint hwnd, DesktopId id)
    {
        if (TryResolve(id) is { } vd) _vdm.MoveViewToDesktop(ViewFor(hwnd), vd);
    }

    public void PinWindow(nint hwnd) => _pinned.PinView(ViewFor(hwnd));

    public void UnpinWindow(nint hwnd) => _pinned.UnpinView(ViewFor(hwnd));

    private IApplicationView ViewFor(nint hwnd)
    {
        int hr = _views.GetViewForHwnd(hwnd, out IApplicationView view);
        if (hr != 0 || view is null)
            throw new COMException($"GetViewForHwnd failed for hwnd 0x{hwnd:X}", hr);
        return view;
    }

    /// <summary>Resolve a Core <see cref="DesktopId"/> to the live COM desktop object, or null if the
    /// OS no longer has that desktop (deleted out from under us).</summary>
    private IVirtualDesktop? TryResolve(DesktopId id)
    {
        Guid g = id.Value;
        return _vdm.FindDesktop(ref g);
    }

    private void SetName(IVirtualDesktop vd, string name)
    {
        nint h = HString.Create(name);
        try { _vdm.SetDesktopName(vd, h); }
        finally { HString.Delete(h); }
    }
}
