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
        var result = new List<WindowInfo>();
        foreach ((nint hwnd, Guid g) in EnumAppWindows())
            if (g == id.Value)
                result.Add(new WindowInfo(hwnd, TitleOf(hwnd), ProcessOf(hwnd)));
        return result;
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
