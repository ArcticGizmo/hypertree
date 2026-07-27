// Spike — THROWAWAY. Answers one question: what does it actually cost to poll
// "which virtual desktop am I on?", and is a real COM notification subscription
// meaningfully cheaper?
//
// The candidate poll is exactly what a watcher would run on a timer:
//     _vdm.GetCurrentDesktop().GetId()
// i.e. one QueryService-cached vtable call returning an IVirtualDesktop RCW, then
// one more call for its GUID. We measure:
//   1. cold cost (first call, RCW + shell warm-up)
//   2. steady-state cost over N iterations (mean / median / p99)
//   3. the same, but only the GetId() half, to see which side dominates
//   4. CPU time consumed by 60s of polling at a candidate interval, extrapolated
//
// Interop block is the 24H2/25H2 definition lifted from the real app's ComInterop.cs.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int Warmup = 200;
    private const int Iterations = 5000;

    [STAThread]
    private static void Main()
    {
        Console.WriteLine($"OS: {Environment.OSVersion.Version}  |  64-bit: {Environment.Is64BitProcess}");
        Console.WriteLine($"Iterations: {Iterations} (after {Warmup} warmup)\n");

        var shellType = Type.GetTypeFromCLSID(Guids.CLSID_ImmersiveShell)
                        ?? throw new InvalidOperationException("No ImmersiveShell CLSID.");

        // ── 1. Cold: connect + first poll ────────────────────────────────────────
        var cold = Stopwatch.StartNew();
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
        var svc = Guids.CLSID_VirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        var vdm = (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iid);
        double connectMs = cold.Elapsed.TotalMilliseconds;

        cold.Restart();
        Guid first = vdm.GetCurrentDesktop().GetId();
        double firstMs = cold.Elapsed.TotalMilliseconds;

        Console.WriteLine($"connect + QueryService : {connectMs,8:F3} ms   (once, at startup)");
        Console.WriteLine($"first poll (cold)      : {firstMs,8:F3} ms   -> {first}");
        Console.WriteLine();

        // ── 2. Steady state: the full poll a watcher would run ───────────────────
        for (int i = 0; i < Warmup; i++) _ = vdm.GetCurrentDesktop().GetId();

        var full = new double[Iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < Iterations; i++)
        {
            sw.Restart();
            _ = vdm.GetCurrentDesktop().GetId();
            full[i] = sw.Elapsed.TotalMilliseconds * 1000.0; // microseconds
        }
        Report("GetCurrentDesktop().GetId()", full);

        // ── 3. Split: how much is GetCurrentDesktop vs GetId? ────────────────────
        var getDesktop = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            sw.Restart();
            _ = vdm.GetCurrentDesktop();
            getDesktop[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
        }
        Report("GetCurrentDesktop() only", getDesktop);

        IVirtualDesktop held = vdm.GetCurrentDesktop();
        var getId = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            sw.Restart();
            _ = held.GetId();
            getId[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
        }
        Report("GetId() only (cached RCW)", getId);

        // ── 4. What does that mean for a timer? ──────────────────────────────────
        double meanUs = full.Average();
        Console.WriteLine("Extrapolated CPU cost of a polling watcher:");
        foreach (int intervalMs in new[] { 100, 250, 300, 500, 1000 })
        {
            double pollsPerHour = 3600_000.0 / intervalMs;
            double cpuSecPerHour = pollsPerHour * meanUs / 1_000_000.0;
            double dutyCycle = meanUs / (intervalMs * 1000.0) * 100.0;
            Console.WriteLine($"  every {intervalMs,5} ms : {cpuSecPerHour,7:F2} s CPU/hour   duty cycle {dutyCycle:F5}%");
        }
        Console.WriteLine();

        // ── 5. Allocation pressure (the real cost of an RCW per tick) ────────────
        long before = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);
        for (int i = 0; i < Iterations; i++) _ = vdm.GetCurrentDesktop().GetId();
        long after = GC.GetTotalAllocatedBytes(precise: true);
        int gen0After = GC.CollectionCount(0);

        double bytesPerPoll = (after - before) / (double)Iterations;
        Console.WriteLine($"allocation : {bytesPerPoll:F0} bytes/poll, {gen0After - gen0Before} gen0 GCs over {Iterations} polls");
        Console.WriteLine($"           : ~{bytesPerPoll * 3600_000.0 / 300 / 1024 / 1024:F1} MB/hour allocated at a 300ms interval");
        Console.WriteLine();

        Console.WriteLine("NOTE: a notification subscription costs ~0 steady-state CPU, but its saving is");
        Console.WriteLine("      the figure above — compare that against the cost of owning an inbound,");
        Console.WriteLine("      per-build COM vtable that the shell calls into.");
    }

    private static void Report(string label, double[] us)
    {
        var sorted = (double[])us.Clone();
        Array.Sort(sorted);
        double mean = us.Average();
        double median = sorted[sorted.Length / 2];
        double p99 = sorted[(int)(sorted.Length * 0.99)];
        double max = sorted[^1];
        Console.WriteLine($"{label,-30} mean {mean,8:F1} us | median {median,8:F1} us | p99 {p99,9:F1} us | max {max,9:F1} us");
    }
}

// ── Build-fragile interop (24H2/25H2) — copied from src/Hypertree.Platform.Windows/ComInterop.cs ──

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
