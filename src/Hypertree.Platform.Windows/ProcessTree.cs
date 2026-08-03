using System.Runtime.InteropServices;
using Hypertree.Launch;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IProcessTree"/> over a Toolhelp process snapshot: read every live process's parent id
/// once, invert it to parent→children, then breadth-first from the root. A single snapshot is a
/// consistent-enough view for the short window a restore polls in; a snapshot it can't take degrades to just
/// the root rather than throwing.
/// </summary>
public sealed class ProcessTree : IProcessTree
{
    public IReadOnlySet<int> DescendantsAndSelf(int rootPid)
    {
        var result = new HashSet<int>();
        if (rootPid <= 0) return result;
        result.Add(rootPid);

        Dictionary<int, List<int>>? children = SnapshotChildren();
        if (children is null) return result;

        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!children.TryGetValue(cur, out List<int>? kids)) continue;
            foreach (int kid in kids)
                if (result.Add(kid)) queue.Enqueue(kid);
        }
        return result;
    }

    // parent pid -> its direct children, for every process in a single snapshot. Null if the snapshot failed.
    private static Dictionary<int, List<int>>? SnapshotChildren()
    {
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return null;
        try
        {
            var byParent = new Dictionary<int, List<int>>();
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return byParent;
            do
            {
                int pid = (int)e.th32ProcessID;
                int parent = (int)e.th32ParentProcessID;
                if (!byParent.TryGetValue(parent, out List<int>? kids))
                    byParent[parent] = kids = new List<int>();
                kids.Add(pid);
            }
            while (Process32Next(snap, ref e));
            return byParent;
        }
        finally { CloseHandle(snap); }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
