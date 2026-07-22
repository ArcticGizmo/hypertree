// M0 Phase 0.1 spike — THROWAWAY. Answers one question: which arrow-based global
// hotkey chords can Hypertree actually own on THIS Windows 11 build?
//
// Two data points per chord:
//   1. ACCEPTED  — RegisterHotKey returned true (the OS let us claim the id).
//   2. FIRES     — pressing it actually delivers WM_HOTKEY to us (proves the shell
//                  isn't eating it first, which is the real risk for Win+Ctrl+Arrow,
//                  Windows' own virtual-desktop switch chord).
//
// A chord that is ACCEPTED but never FIRES (or fires *and* still switches desktop) is
// no good — it means the OS wins the race. We want ACCEPTED + FIRES + no side effect.
//
// Uses a dedicated message-loop thread per the perch GlobalHotkey pattern, but here we
// register many chords on one loop and just log presses. Ctrl+C to quit.

using System.Runtime.InteropServices;

const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
const uint VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
const int WM_HOTKEY = 0x0312;

// Candidate chord sets, most-desired first. Win+Ctrl+Arrow is the native desktop-switch
// chord — we EXPECT collision on ←/→ at least; ↑/↓ may be free. The alternates are the
// fallback layers from the plan if Win+Ctrl is unusable.
(string mods, uint mod)[] sets =
[
    ("Win+Ctrl",       MOD_WIN | MOD_CONTROL),
    ("Alt+Ctrl",       MOD_ALT | MOD_CONTROL),
    ("Win+Alt",        MOD_WIN | MOD_ALT),
    ("Ctrl+Alt+Shift", MOD_CONTROL | MOD_ALT | MOD_SHIFT),
    ("Win+Shift",      MOD_WIN | MOD_SHIFT),
];
(string name, uint vk)[] arrows = [("Left", VK_LEFT), ("Up", VK_UP), ("Right", VK_RIGHT), ("Down", VK_DOWN)];

var registry = new Dictionary<int, string>();
int id = 1;

var thread = new Thread(() =>
{
    // Register everything on this thread (WM_HOTKEY lands on the registering thread).
    Console.WriteLine("Registering candidate chords (ACCEPTED = OS let us claim it):\n");
    foreach (var (mods, mod) in sets)
    {
        foreach (var (aname, vk) in arrows)
        {
            int thisId = id++;
            bool ok = RegisterHotKey(IntPtr.Zero, thisId, mod | MOD_NOREPEAT, vk);
            string label = $"{mods}+{aname}";
            Console.WriteLine($"  [{(ok ? "ACCEPTED" : "REFUSED ")}] {label}");
            if (ok) registry[thisId] = label;
        }
        Console.WriteLine();
    }

    Console.WriteLine("Now PRESS each accepted chord. Lines below = chords that actually FIRE.");
    Console.WriteLine("Watch for side effects too (did the desktop also switch?).\n");
    Console.WriteLine(new string('-', 60));

    while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
    {
        if (msg.message == WM_HOTKEY && registry.TryGetValue((int)msg.wParam, out var label))
            Console.WriteLine($"  FIRES  ->  {label}");
    }
}) { IsBackground = true, Name = "HypertreeSpikeHotkeys" };

thread.Start();

Console.WriteLine("\nPress Ctrl+C to quit.\n");
var quit = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();

[DllImport("user32.dll", SetLastError = true)]
static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

[DllImport("user32.dll")]
static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
    public uint time; public int ptX; public int ptY;
}
