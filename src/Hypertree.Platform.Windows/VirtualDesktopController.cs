using System.Runtime.InteropServices;
using Hypertree.Desktops;

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

    public VirtualDesktopController()
    {
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

    // Switch/rename/remove tolerate a desktop that no longer exists (e.g. the user deleted it from
    // Task View): the id is stale, so there's nothing to do — no-op rather than crash the tray. The
    // navigation model reconciles the stale record separately.
    public void SwitchTo(DesktopId id)
    {
        if (TryResolve(id) is { } vd) _vdm.SwitchDesktop(vd);
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
