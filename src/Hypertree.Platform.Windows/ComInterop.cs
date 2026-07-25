using System.Runtime.InteropServices;

namespace Hypertree.Platform.Windows;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE build-fragile surface. Everything Hypertree depends on that changes per Windows build lives
// in this one file: the undocumented virtual-desktop COM interfaces and their GUIDs. Verified on
// Windows 11 build 26200 (25H2 — shares the 24H2 kernel). Interface definitions transcribed from
// public reverse-engineered references (MScholtes/VirtualDesktop VirtualDesktop11-24H2.cs); no
// third-party binary is loaded. See docs/design/m0-findings.md.
//
// If a future build breaks this, update ONLY this file (ideally add a per-build GUID table). The
// vtable ORDER of each interface's members is load-bearing — every method up to the last one we
// call must be declared in exact order so the offsets line up; unused interface-pointer params
// use nint placeholders.
// ─────────────────────────────────────────────────────────────────────────────────────────────

internal static class Guids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid CLSID_VirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

    // The PUBLIC, documented virtual-desktop API — stable since Windows 10, unlike the internal ones
    // above. Used only to ask which desktop a given window is on (for the map's per-desktop counts).
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

// Documented API (shell32) — CoCreatable, not from the ImmersiveShell service provider. Only
// GetWindowDesktopId is used; the other two members are declared to keep the vtable order correct.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, out int onCurrent);
    [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(nint hwnd, ref Guid desktopId);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);
    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(nint view);
    Guid GetId();
    nint GetName();          // HSTRING — read via HString.Read
    nint GetWallpaperPath(); // HSTRING
    bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();                                                             // 0
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);     // 1
    bool CanViewMoveDesktops(nint view);                                        // 2
    IVirtualDesktop GetCurrentDesktop();                                        // 3
    void GetDesktops(out IObjectArray desktops);                               // 4
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int dir, out IVirtualDesktop d); // 5
    void SwitchDesktop(IVirtualDesktop desktop);                                // 6
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);           // 7
    IVirtualDesktop CreateDesktop();                                            // 8
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);                      // 9
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);      // 10
    IVirtualDesktop FindDesktop(ref Guid desktopId);                            // 11
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop d, out IObjectArray a, out IObjectArray b); // 12
    void SetDesktopName(IVirtualDesktop desktop, nint name /* HSTRING */);      // 13
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    [PreserveSig] int GetViews(out nint array);
    [PreserveSig] int GetViewsByZOrder(out nint array);
    [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out nint array);
    [PreserveSig] int GetViewForHwnd(nint hwnd, out IApplicationView view);
}

// Opaque — only obtained and passed back to MoveViewToDesktop / PinView; no methods called.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView { }

// Pin/unpin a window (via its view) to all desktops — keeps the overlay visible across switches.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
internal interface IVirtualDesktopPinnedApps
{
    bool IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId);
    void PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    void UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    bool IsViewPinned(IApplicationView view);
    void PinView(IApplicationView view);
    void UnpinView(IApplicationView view);
}
