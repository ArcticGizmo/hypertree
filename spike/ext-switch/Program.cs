// Spike — THROWAWAY test instrument. Switches the current virtual desktop by ORDINAL, going straight to
// the shell COM without involving Hypertree.
//
// That is the whole point: it stands in for Win+Ctrl+Arrow, Task View, or another launcher jumping to one
// of its windows. From Hypertree's side this is indistinguishable from a user doing it by hand, so it's
// what proves the ambient desktop watcher actually notices a switch nobody told it about — the thing the
// tray used to stay stale about until the next hotkey.
//
//   ext-switch          list the desktops and say which one is current
//   ext-switch <n>      switch to desktop ordinal n (0-based)
//
// Interop block copied from src/Hypertree.Platform.Windows/ComInterop.cs (24H2/25H2).

using System;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var shellType = Type.GetTypeFromCLSID(Guids.CLSID_ImmersiveShell)
                        ?? throw new InvalidOperationException("No ImmersiveShell CLSID.");
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
        var svc = Guids.CLSID_VirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        var vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iid);

        vdm.GetDesktops(out IObjectArray arr);
        arr.GetCount(out int count);
        var deskIid = typeof(IVirtualDesktop).GUID;
        Guid current = vdm.GetCurrentDesktop().GetId();

        if (args.Length == 0)
        {
            for (int i = 0; i < count; i++)
            {
                arr.GetAt(i, ref deskIid, out object o);
                var vd = (IVirtualDesktop)o;
                Guid id = vd.GetId();
                Console.WriteLine($"{(id == current ? "*" : " ")} {i}  {id}");
            }
            return 0;
        }

        if (!int.TryParse(args[0], out int target) || target < 0 || target >= count)
        {
            Console.Error.WriteLine($"ext-switch: ordinal must be 0..{count - 1}");
            return 2;
        }

        arr.GetAt(target, ref deskIid, out object chosen);
        vdm.SwitchDesktop((IVirtualDesktop)chosen);
        Console.WriteLine($"switched to ordinal {target}");
        return 0;
    }
}

internal static class Guids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
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
    nint GetName();
    nint GetWallpaperPath();
    bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(nint view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int dir, out IVirtualDesktop d);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop d, out IObjectArray a, out IObjectArray b);
    void SetDesktopName(IVirtualDesktop desktop, nint name);
}
