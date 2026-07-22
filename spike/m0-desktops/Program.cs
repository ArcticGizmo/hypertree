// M0 Phase 0.2 spike — THROWAWAY. Answers: can Hypertree drive Windows virtual desktops
// through OUR OWN COM interop (no third-party DLL) on THIS build (26200 / 25H2)?
//
// It talks to the ImmersiveShell's undocumented IVirtualDesktopManagerInternal. The
// GUIDs + vtable order below are the 24H2/25H2 definitions (build 26100/26200 share a
// kernel) taken from public reverse-engineered references (MScholtes/VirtualDesktop
// VirtualDesktop11-24H2.cs). We reimplement the *interface definitions* as our own code
// — nothing external is loaded or executed.
//
// Sequence (all reversible, cleans up after itself):
//   1. connect to shell, get the internal manager
//   2. print count + current desktop + list all
//   3. create a desktop, name it "hypertree-spike"
//   4. switch to it  (VISIBLE: your view flips — watch whether ALL monitors move together)
//   5. switch back to where you started (always, via finally)
//   6. remove the created desktop (fallback = original)
//
// The whole GUID/vtable block is the ONE build-fragile thing; in the real app it lives
// behind IDesktopController so an OS update is a one-file swap.

using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Console.WriteLine("Connecting to ImmersiveShell…");
            var shellType = Type.GetTypeFromCLSID(Guids.CLSID_ImmersiveShell)
                            ?? throw new InvalidOperationException("No ImmersiveShell CLSID.");
            var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;

            var svc = Guids.CLSID_VirtualDesktopManagerInternal;
            var iid = typeof(IVirtualDesktopManagerInternal).GUID;
            var vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iid);
            Console.WriteLine("  got IVirtualDesktopManagerInternal ✓\n");

            // 2 — read state
            int count = vdm.GetCount();
            IVirtualDesktop current = vdm.GetCurrentDesktop();
            Console.WriteLine($"Desktop count: {count}");
            Console.WriteLine($"Current: {Describe(current)}\n");

            Console.WriteLine("All desktops:");
            vdm.GetDesktops(out IObjectArray arr);
            arr.GetCount(out int n);
            var iidVd = typeof(IVirtualDesktop).GUID;
            for (int i = 0; i < n; i++)
            {
                arr.GetAt(i, ref iidVd, out object o);
                Console.WriteLine($"  [{i}] {Describe((IVirtualDesktop)o)}");
            }
            Console.WriteLine();

            // 3 — create + name (name via manual HSTRING; .NET5+ dropped UnmanagedType.HString)
            Console.WriteLine("Creating desktop \"hypertree-spike\"…");
            IVirtualDesktop created = vdm.CreateDesktop();
            IntPtr hName = HString.Create("hypertree-spike");
            try { vdm.SetDesktopName(created, hName); Console.WriteLine("  named ✓"); }
            catch (Exception ex) { Console.WriteLine($"  SetDesktopName failed: {ex.Message}"); }
            finally { HString.Delete(hName); }
            Console.WriteLine($"  new count: {vdm.GetCount()}, created = {Describe(created)}\n");

            // 4 + 5 — switch there, then ALWAYS switch back
            Console.WriteLine(">>> Switching to the new desktop (watch all monitors) …");
            try
            {
                vdm.SwitchDesktop(created);
                Thread.Sleep(1500);
            }
            finally
            {
                Console.WriteLine(">>> Switching back to original …");
                vdm.SwitchDesktop(current);
                Thread.Sleep(300);
            }

            // 6 — clean up
            Console.WriteLine("Removing the spike desktop (fallback = original)…");
            try { vdm.RemoveDesktop(created, current); Console.WriteLine($"  removed ✓, count back to {vdm.GetCount()}"); }
            catch (Exception ex) { Console.WriteLine($"  RemoveDesktop failed (harmless, delete it manually): {ex.Message}"); }

            Console.WriteLine("\nRESULT: create + name + switch + switch-back + remove all exercised via our own interop.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFAILED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("If this is an InvalidCastException/E_NOINTERFACE, the GUID/vtable doesn't match");
            Console.WriteLine("this build — that's the exact 0.4 native-vs-komorebi risk. Note it and move on.");
            Environment.ExitCode = 1;
        }
    }

    private static string Describe(IVirtualDesktop d)
    {
        string name;
        try { name = HString.Read(d.GetName()); } catch { name = "<name unavailable>"; }
        if (string.IsNullOrEmpty(name)) name = "(unnamed)";
        return $"{name}  {d.GetId():B}";
    }
}

// Manual HSTRING marshalling via combase.dll — .NET 5+ removed the built-in
// UnmanagedType.HString marshaller, so the internal manager's name APIs need this.
internal static class HString
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    public static IntPtr Create(string s)
    {
        WindowsCreateString(s, s.Length, out IntPtr h);
        return h;
    }

    public static void Delete(IntPtr h)
    {
        if (h != IntPtr.Zero) WindowsDeleteString(h);
    }

    public static string Read(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        IntPtr buf = WindowsGetStringRawBuffer(h, out uint len);
        return len == 0 ? "" : Marshal.PtrToStringUni(buf, (int)len);
    }
}

internal static class Guids
{
    // ImmersiveShell object + the service key for the internal manager (25H2/build 26200).
    public static readonly Guid CLSID_ImmersiveShell =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal =
        new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
}

// Shell's IServiceProvider (classic OLE IID). QueryService bridges to the VD manager.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

// Minimal enumerator returned by GetDesktops.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);
    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}

// IVirtualDesktop — 24H2/25H2 vtable order. We only call GetId/GetName; the rest keep
// the slots aligned.
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IntPtr view);
    Guid GetId();
    IntPtr GetName();          // HSTRING — read via HString.Read
    IntPtr GetWallpaperPath(); // HSTRING
    bool IsRemote();
}

// IVirtualDesktopManagerInternal — 24H2/25H2 vtable order. Every method up to the last
// one we call MUST be declared in exact order so the vtable offsets line up; unused
// slots take IntPtr for interface-pointer params (ABI-compatible).
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();                                                        // 0
    void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);          // 1
    bool CanViewMoveDesktops(IntPtr view);                                 // 2
    IVirtualDesktop GetCurrentDesktop();                                   // 3
    void GetDesktops(out IObjectArray desktops);                           // 4
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int dir, out IVirtualDesktop d); // 5
    void SwitchDesktop(IVirtualDesktop desktop);                           // 6
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);      // 7
    IVirtualDesktop CreateDesktop();                                       // 8
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);                 // 9
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback); // 10
    IVirtualDesktop FindDesktop(ref Guid desktopId);                       // 11
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop d, out IObjectArray a, out IObjectArray b); // 12
    void SetDesktopName(IVirtualDesktop desktop, IntPtr name /* HSTRING */);                             // 13
}
