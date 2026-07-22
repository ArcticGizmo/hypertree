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

    public void SwitchTo(DesktopId id) => _vdm.SwitchDesktop(Resolve(id));

    public DesktopId Create(string name)
    {
        IVirtualDesktop vd = _vdm.CreateDesktop();
        SetName(vd, name);
        return new DesktopId(vd.GetId());
    }

    public void Rename(DesktopId id, string name) => SetName(Resolve(id), name);

    public void Remove(DesktopId id, DesktopId fallback) => _vdm.RemoveDesktop(Resolve(id), Resolve(fallback));

    public string GetName(DesktopId id) => HString.Read(Resolve(id).GetName());

    public void MoveWindowToDesktop(nint hwnd, DesktopId id)
        => _vdm.MoveViewToDesktop(ViewFor(hwnd), Resolve(id));

    public void PinWindow(nint hwnd) => _pinned.PinView(ViewFor(hwnd));

    public void UnpinWindow(nint hwnd) => _pinned.UnpinView(ViewFor(hwnd));

    private IApplicationView ViewFor(nint hwnd)
    {
        int hr = _views.GetViewForHwnd(hwnd, out IApplicationView view);
        if (hr != 0 || view is null)
            throw new COMException($"GetViewForHwnd failed for hwnd 0x{hwnd:X}", hr);
        return view;
    }

    /// <summary>Resolve a Core <see cref="DesktopId"/> to the live COM desktop object.</summary>
    private IVirtualDesktop Resolve(DesktopId id)
    {
        Guid g = id.Value;
        return _vdm.FindDesktop(ref g)
               ?? throw new ArgumentException($"No virtual desktop with id {id}.", nameof(id));
    }

    private void SetName(IVirtualDesktop vd, string name)
    {
        nint h = HString.Create(name);
        try { _vdm.SetDesktopName(vd, h); }
        finally { HString.Delete(h); }
    }
}
