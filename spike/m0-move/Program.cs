// M0 Phase 0.3 spike — THROWAWAY. Answers: can Hypertree move a FOREIGN window (one it
// didn't create) onto another virtual desktop? That's what provisioning a scope's trio
// (terminal / editor / browser) requires.
//
// Tries the DOCUMENTED, build-stable IVirtualDesktopManager.MoveWindowToDesktop(hwnd,
// guid) first — best case, zero build risk. It's known to sometimes return
// E_ACCESSDENIED (0x80070005) for windows the caller doesn't own; if so, that tells us
// we need the internal IApplicationView path (build-fragile) and we note it for 0.4.
//
// Sequence (self-cleaning):
//   1. launch a real, separate Notepad (a foreign window), grab its hwnd
//   2. create a target desktop, read its GUID
//   3. MoveWindowToDesktop(notepad, targetGuid)  — capture the HRESULT
//   4. verify via GetWindowDesktopId + IsWindowOnCurrentVirtualDesktop
//   5. clean up: kill Notepad, remove the desktop

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Process? pad = null;
        var shellType = Type.GetTypeFromCLSID(Guids.CLSID_ImmersiveShell)!;
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;

        var svc = Guids.CLSID_VirtualDesktopManagerInternal;
        var iidInternal = typeof(IVirtualDesktopManagerInternal).GUID;
        var vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iidInternal);

        // Application-view collection (same shell; service == riid == its own IID).
        var avcGuid = typeof(IApplicationViewCollection).GUID;
        var avc = (IApplicationViewCollection)shell.QueryService(ref avcGuid, ref avcGuid);

        // Documented manager — plain CoCreate, no shell service needed.
        var docType = Type.GetTypeFromCLSID(Guids.CLSID_VirtualDesktopManager)!;
        var docMgr = (IVirtualDesktopManager)Activator.CreateInstance(docType)!;

        IVirtualDesktop? target = null;
        IVirtualDesktop original = vdm.GetCurrentDesktop();
        try
        {
            // 1 — foreign window: charmap is a genuine classic Win32 app (single process,
            // real window handle, no save prompt on kill) — unlike Win11's Store Notepad,
            // which reparents to another process and never populates MainWindowHandle.
            Console.WriteLine("Launching Character Map (a foreign Win32 window)…");
            pad = Process.Start(new ProcessStartInfo(
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\charmap.exe"))
            { UseShellExecute = true });
            IntPtr hwnd = WaitForWindow(pad!);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("  couldn't get the window handle — aborting."); return; }
            Console.WriteLine($"  hwnd = 0x{hwnd.ToInt64():X}  (pid {pad!.Id})");

            // 2 — target desktop
            target = vdm.CreateDesktop();
            IntPtr hn = HString.Create("hypertree-move-test");
            try { vdm.SetDesktopName(target, hn); } finally { HString.Delete(hn); }
            Guid targetId = target.GetId();
            Console.WriteLine($"  target desktop = {targetId:B}\n");

            // 3a — try the DOCUMENTED (stable) API first; capture the raw HRESULT.
            Console.WriteLine("Attempt A — MoveWindowToDesktop (documented, stable API)…");
            int hr = docMgr.MoveWindowToDesktop(hwnd, ref targetId);
            Console.WriteLine($"  HRESULT = 0x{hr & 0xFFFFFFFFL:X8}  ({(hr == 0 ? "S_OK" : Describe(hr))})");
            bool movedA = docMgr.GetWindowDesktopId(hwnd) == targetId && !docMgr.IsWindowOnCurrentVirtualDesktop(hwnd);
            Console.WriteLine($"  stuck? {movedA}\n");

            // 3b — the real path for FOREIGN windows: resolve the hwnd to an IApplicationView
            // and move the view. This is the build-fragile internal API.
            bool movedB = movedA;
            if (!movedA)
            {
                Console.WriteLine("Attempt B — IApplicationViewCollection.GetViewForHwnd → MoveViewToDesktop (internal)…");
                int ghr = avc.GetViewForHwnd(hwnd, out IApplicationView view);
                if (ghr != 0 || view == null)
                {
                    Console.WriteLine($"  GetViewForHwnd failed: 0x{ghr & 0xFFFFFFFFL:X8}");
                }
                else
                {
                    vdm.MoveViewToDesktop(view, target);
                    movedB = docMgr.GetWindowDesktopId(hwnd) == targetId && !docMgr.IsWindowOnCurrentVirtualDesktop(hwnd);
                    Console.WriteLine($"  GetWindowDesktopId -> {docMgr.GetWindowDesktopId(hwnd):B}");
                    Console.WriteLine($"  IsWindowOnCurrentVirtualDesktop -> {docMgr.IsWindowOnCurrentVirtualDesktop(hwnd)}");
                    Console.WriteLine($"  stuck? {movedB}");
                }
            }

            Console.WriteLine();
            if (movedA)
                Console.WriteLine("RESULT ✅  moved via the STABLE documented API (no build risk).");
            else if (movedB)
                Console.WriteLine("RESULT ✅  foreign window moved via the internal IApplicationView path.\n" +
                                  "           Works — but it's the build-fragile API; isolate behind IDesktopController.");
            else
                Console.WriteLine("RESULT ❌  could not move a foreign window by either path — flag for the 0.4 gate.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFAILED: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally
        {
            try { if (target != null) vdm.RemoveDesktop(target, original); } catch { }
            try { pad?.Kill(); } catch { }
            Console.WriteLine("\ncleaned up (removed test desktop, closed the test window).");
        }
    }

    // Poll for the process's top-level window; MainWindowHandle can lag the launch.
    // Falls back to an EnumWindows scan keyed on the pid if MainWindowHandle stays 0.
    private static IntPtr WaitForWindow(Process p)
    {
        for (int i = 0; i < 60; i++)
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;

            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == (uint)p.Id) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;

            Thread.Sleep(75);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

    private static string Describe(int hr) => (uint)hr switch
    {
        0x80070005 => "E_ACCESSDENIED — caller can't move this foreign window; needs IApplicationView path",
        0x80070057 => "E_INVALIDARG",
        _ => "unexpected"
    };
}

internal static class Guids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

// Documented, build-STABLE manager (shobjidl_core). PreserveSig on the move so we can
// read the exact HRESULT instead of eating a COMException.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
    Guid GetWindowDesktopId(IntPtr topLevelWindow);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IntPtr view);
    Guid GetId();
    IntPtr GetName();
    IntPtr GetWallpaperPath();
    bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(IntPtr view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IntPtr desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int dir, out IVirtualDesktop d);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop d, out IntPtr a, out IntPtr b);
    void SetDesktopName(IVirtualDesktop desktop, IntPtr name /* HSTRING */);
}

// Resolves an hwnd to the shell's view object so the internal manager can move it.
// GetViewForHwnd must sit at vtable slot 3 — the three GetViews* methods before it are
// declared only to hold the slots (out IObjectArray → out IntPtr placeholder).
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    [PreserveSig] int GetViews(out IntPtr array);
    [PreserveSig] int GetViewsByZOrder(out IntPtr array);
    [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
    [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
}

// Opaque — we only obtain it and pass it back; no methods called, so the IID (for QI)
// is all that's needed.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView { }

internal static class HString
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static IntPtr Create(string s) { WindowsCreateString(s, s.Length, out IntPtr h); return h; }
    public static void Delete(IntPtr h) { if (h != IntPtr.Zero) WindowsDeleteString(h); }
}
