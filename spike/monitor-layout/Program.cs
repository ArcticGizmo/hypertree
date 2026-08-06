// Monitor-layout-restore spike — THROWAWAY. Proves the four hard bits from
// docs/design/monitor-layout-restore.md before any of it touches the app:
//
//   #1 monitor identity across a dock cycle  — stable ids via QueryDisplayConfig (NOT monitor index)
//   #2 window identity                        — HWND, same-session (the easy, headline path)
//   #3 timing                                 — WM_DISPLAYCHANGE on a message-only window, debounced
//   #4 DPI + untouchable windows              — PerMonitorV2 (app.manifest), best-effort SetWindowPlacement
//
// Commands:
//   monitor-layout list       — enumerate monitors with their STABLE ids (run docked vs undocked; ids stay put)
//   monitor-layout snap        — snapshot current window placements -> %TEMP%\ht-layout-spike.json
//   monitor-layout restore     — put windows back from that snapshot
//   monitor-layout watch       — event loop: auto-snap on undock, OFFER restore on redock (the real flow)
//
// The snapshot is keyed by the present monitor SET, so `watch` restores the layout that belongs to the
// dock you just reconnected — not whatever was last saved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Program
{
    private static readonly string StateFile =
        Path.Combine(Path.GetTempPath(), "ht-layout-spike.json");

    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (cmd)
        {
            case "list":    ListMonitors(); return 0;
            case "snap":    Snap();         return 0;
            case "restore": Restore();      return 0;
            case "watch":   Watch();        return 0;
            case "diag":    Diag();         return 0;
            case "selftest": return SelfTest();
            default:
                Console.WriteLine("usage: monitor-layout [list|snap|restore|watch]");
                return 3;
        }
    }

    // ── #1 Monitor identity ────────────────────────────────────────────────────────────────────────
    // Each HMONITOR carries a GDI device name (\\.\DISPLAY1) that reshuffles across dock cycles. We chain
    // it through QueryDisplayConfig to the target's DEVICE PATH — an EDID-derived key that stays put — so
    // "the left 4K" is the same MonitorRef.stableId before and after undocking.
    private static void ListMonitors()
    {
        var mons = EnumMonitors();
        Console.WriteLine($"{mons.Count} monitor(s) present — set key: {MonitorSetKey(mons)}\n");
        foreach (var m in mons)
        {
            Console.WriteLine($"  {(m.IsPrimary ? "*" : " ")} {m.Friendly}");
            Console.WriteLine($"      stableId : {m.StableId}");
            Console.WriteLine($"      gdiName  : {m.GdiName}   (this one DOES shuffle — never key on it)");
            Console.WriteLine($"      bounds   : {m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height}   dpi {m.Dpi}");
            Console.WriteLine();
        }
    }

    private static void Snap()
    {
        var mons = EnumMonitors();
        var wins = SnapshotWindows(mons);
        var doc = new Snapshot(MonitorSetKey(mons), mons, wins);
        File.WriteAllText(StateFile, SnapshotJson.Write(doc));
        Console.WriteLine($"Snapped {wins.Count} window(s) across {mons.Count} monitor(s) -> {StateFile}");
        foreach (var w in wins)
            Console.WriteLine($"  [{w.Show,-9}] {Short(w.MonitorStableId)}  {w.Title}");
    }

    private static void Restore()
    {
        if (!File.Exists(StateFile)) { Console.WriteLine("no snapshot yet — run `snap` first."); return; }
        var doc = SnapshotJson.Read(File.ReadAllText(StateFile));
        RestoreWindows(doc);
    }

    // ── #3 Timing ──────────────────────────────────────────────────────────────────────────────────
    // A message-only window receives WM_DISPLAYCHANGE. It fires MID-reshuffle and often several times per
    // physical dock, so we never act on the raw event: we debounce, then compare the settled monitor set
    // against the last one we saw. Fewer monitors => undock => save. A set we hold a snapshot for => offer.
    private static void Watch()
    {
        Console.WriteLine("watching for dock/undock — Ctrl+C to stop.\n");
        var current = EnumMonitors();
        var known = new Dictionary<string, Snapshot>();
        LoadKnown(known);
        string lastKey = MonitorSetKey(current);
        Console.WriteLine($"start: {current.Count} monitor(s), set {lastKey}\n");

        // rolling "last good" layout so an undock doesn't have to race the shell to read placements
        var lastGood = SnapshotWindows(current);

        // The settled-topology handler. Reached two ways (see below): the WM_DISPLAYCHANGE broadcast, and a
        // 2 s poll backstop — either way it debounces, then acts only if the monitor SET actually changed.
        void OnMaybeChanged(string via)
        {
            Debounce(750, () =>
            {
                var now = EnumMonitors();
                string key = MonitorSetKey(now);
                if (key == lastKey) { lastGood = SnapshotWindows(now); return; } // same set, refresh rolling snapshot
                Console.WriteLine($"[{via}] topology settled: {now.Count} monitor(s), set {key}");

                if (now.Count < current.Count)
                {
                    // UNDOCK — persist the layout we were holding, under the set we're LEAVING
                    var doc = new Snapshot(lastKey, current, lastGood);
                    known[lastKey] = doc;
                    File.WriteAllText(SetPath(lastKey), SnapshotJson.Write(doc));
                    Console.WriteLine($"[undock] saved {lastGood.Count}-window layout for set {lastKey}");
                }
                else if (known.TryGetValue(key, out var snap))
                {
                    // REDOCK to a set we know — OFFER (opt-in per event, per the design)
                    Console.WriteLine($"[redock] set {key} — snapshot available ({snap.Windows.Count} windows).");
                    Console.Write("          restore now? [y/N] ");
                    var line = Console.ReadLine();
                    if (line?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true)
                        RestoreWindows(snap);
                    else
                        Console.WriteLine("          skipped.");
                }
                else
                {
                    Console.WriteLine($"[redock] set {key} — no snapshot on file, nothing to offer.");
                }

                current = now; lastKey = key; lastGood = SnapshotWindows(now);
            });
        }

        // Backstop: poll the monitor set every 2 s. Independent of any window message, so detection never
        // relies solely on the broadcast arriving. In the real app this is the lazy timer that also keeps
        // the rolling "current layout" fresh — here it doubles as the guaranteed dock/undock trigger.
        var poll = new Timer(_ =>
        {
            if (MonitorSetKey(EnumMonitors()) != lastKey) OnMaybeChanged("poll");
        }, null, 2000, 2000);
        GC.KeepAlive(poll);

        // WM_DISPLAYCHANGE on a real (hidden) top-level window — NOT a message-only window, which is
        // excluded from broadcasts and never sees it. This is the low-latency signal; the poll is the net.
        RunMessageWindow(onDisplayChange: () =>
        {
            Console.WriteLine("(WM_DISPLAYCHANGE received)");
            OnMaybeChanged("event");
        });
    }

    // ── Self-contained round-trip proof (touches only its own launched window) ───────────────────────
    // Launches charmap (a genuine classic Win32 window — see spike/m0-move findings), then exercises the
    // real snapshot->restore coordinate math by moving it ACROSS monitors and back, verifying against
    // MonitorFromWindow each time. Proves the offset re-anchoring (#2/#4) without disturbing live windows.
    private static int SelfTest()
    {
        var mons = EnumMonitors();
        if (mons.Count < 2) { Console.WriteLine("selftest wants ≥2 monitors to prove cross-monitor restore; have " + mons.Count); return 1; }

        var pad = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\charmap.exe")) { UseShellExecute = true });
        nint hwnd = 0;
        for (int i = 0; i < 60 && hwnd == 0; i++) { pad!.Refresh(); hwnd = pad.MainWindowHandle; if (hwnd == 0) Thread.Sleep(75); }
        if (hwnd == 0) { Console.WriteLine("couldn't get test window"); return 1; }

        try
        {
            var byGdi = mons.ToDictionary(m => m.GdiName, StringComparer.OrdinalIgnoreCase);
            var start = MonitorOf(hwnd, byGdi);
            var other = mons.First(m => m.StableId != start.StableId);
            Console.WriteLine($"test window opened on {start.Friendly}");

            // capture placement, compute the monitor-relative offset exactly as Snap does
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref wp);
            int offL = wp.rcNormalPosition.left - start.Bounds.Left, offT = wp.rcNormalPosition.top - start.Bounds.Top;
            int w = wp.rcNormalPosition.right - wp.rcNormalPosition.left, h = wp.rcNormalPosition.bottom - wp.rcNormalPosition.top;

            // "restore" onto the OTHER monitor: target origin + same offset
            Place(hwnd, other.Bounds.Left + offL, other.Bounds.Top + offT, w, h);
            Thread.Sleep(300);
            var landed = MonitorOf(hwnd, byGdi);
            bool movedOk = landed.StableId == other.StableId;
            Console.WriteLine($"  moved to {other.Friendly}: landed on {landed.Friendly}  {(movedOk ? "✅" : "❌")}");

            // "restore" back onto the original monitor
            Place(hwnd, start.Bounds.Left + offL, start.Bounds.Top + offT, w, h);
            Thread.Sleep(300);
            var back = MonitorOf(hwnd, byGdi);
            bool backOk = back.StableId == start.StableId;
            Console.WriteLine($"  restored to {start.Friendly}: landed on {back.Friendly}  {(backOk ? "✅" : "❌")}");

            Console.WriteLine(movedOk && backOk
                ? "\nRESULT ✅  offset-based cross-monitor restore works against stable monitor ids."
                : "\nRESULT ❌  restore did not land on the intended physical monitor.");
            return movedOk && backOk ? 0 : 1;
        }
        finally { try { pad?.Kill(); } catch { } Console.WriteLine("(closed test window)"); }
    }

    private static MonitorRef MonitorOf(nint hwnd, Dictionary<string, MonitorRef> byGdi)
    {
        string gdi = GdiNameOf(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        return byGdi.TryGetValue(gdi, out var m) ? m : new MonitorRef("?", gdi, gdi, default, false, 96);
    }

    private static void Place(nint hwnd, int left, int top, int w, int h)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref wp);
        wp.showCmd = SW_RESTORE;
        wp.rcNormalPosition = new RECT { left = left, top = top, right = left + w, bottom = top + h };
        SetWindowPlacement(hwnd, ref wp);
    }

    // ── #2 + #4 Snapshot / restore windows ───────────────────────────────────────────────────────────
    private static List<WinPlace> SnapshotWindows(List<MonitorRef> mons)
    {
        var byGdi = mons.ToDictionary(m => m.GdiName, StringComparer.OrdinalIgnoreCase);
        var result = new List<WinPlace>();
        uint own = GetCurrentProcessId();
        EnumWindows((hwnd, _) =>
        {
            if (!IsCountableWindow(hwnd, own)) return true;
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return true;

            // which monitor: map HMONITOR -> gdi name -> our stable id + bounds
            nint hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            string gdi = GdiNameOf(hmon);
            byGdi.TryGetValue(gdi, out var m);
            string stableId = m?.StableId ?? "?";
            int monLeft = m?.Bounds.Left ?? 0, monTop = m?.Bounds.Top ?? 0;

            // Store the rect as an offset from THIS window's monitor origin, so restore can re-anchor it to
            // wherever that monitor lands next dock. Round-trip on the same monitor is exact (offset cancels).
            result.Add(new WinPlace(
                (long)hwnd, stableId, TitleOf(hwnd),
                new Rect(wp.rcNormalPosition.left - monLeft, wp.rcNormalPosition.top - monTop,
                         wp.rcNormalPosition.right - wp.rcNormalPosition.left,
                         wp.rcNormalPosition.bottom - wp.rcNormalPosition.top),
                ShowStateOf(wp.showCmd)));
            return true;
        }, 0);
        return result;
    }

    private static void RestoreWindows(Snapshot doc)
    {
        var byStable = EnumMonitors().ToDictionary(m => m.StableId, m => m, StringComparer.OrdinalIgnoreCase);
        int ok = 0, gone = 0, noMon = 0, refused = 0;
        foreach (var w in doc.Windows)
        {
            nint hwnd = (nint)w.Hwnd;
            if (!IsWindow(hwnd)) { gone++; continue; } // #2: same-session HWND match; closed window = skip

            if (!byStable.TryGetValue(w.MonitorStableId, out var mon)) { noMon++; continue; } // that screen isn't back

            // #4: normalRect is workarea-relative & scale-stable; drop it onto the target monitor's origin,
            // then apply the remembered show state. Best-effort — elevated/UWP windows may refuse.
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) { refused++; continue; }
            int width = w.Normal.Width, height = w.Normal.Height;
            int left = mon.Bounds.Left + w.Normal.Left;   // w.Normal is the monitor-relative offset from snap
            int top  = mon.Bounds.Top  + w.Normal.Top;
            wp.rcNormalPosition = new RECT { left = left, top = top, right = left + width, bottom = top + height };
            wp.showCmd = w.Show switch { "Maximized" => SW_MAXIMIZE, "Minimized" => SW_MINIMIZE, _ => SW_RESTORE };
            if (SetWindowPlacement(hwnd, ref wp)) ok++; else refused++;
        }
        Console.WriteLine($"[restore] {ok} placed, {gone} gone, {noMon} monitor-missing, {refused} refused.");
    }

    // ── Monitor enumeration + the stable-id chain ────────────────────────────────────────────────────
    private static List<MonitorRef> EnumMonitors()
    {
        // gdi name -> (stable device path, friendly name), via QueryDisplayConfig
        var stable = BuildStableIdMap();
        var list = new List<MonitorRef>();
        EnumDisplayMonitors(0, 0, (hmon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hmon, ref mi)) return true;
            string gdi = mi.szDevice;
            GetDpiForMonitor(hmon, 0, out uint dpiX, out _);
            var (path, friendly) = stable.TryGetValue(gdi, out var s) ? s : (gdi, gdi);
            list.Add(new MonitorRef(
                path, friendly.Length == 0 ? gdi : friendly, gdi,
                new Rect(mi.rcMonitor.left, mi.rcMonitor.top,
                         mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top),
                (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, dpiX));
            return true;
        }, 0);
        return list.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    // Temporary diagnostic — trace every step of the stable-id chain with its raw return code.
    private static void Diag()
    {
        Console.WriteLine($"sizeof MODE_INFO   = {Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>()} (want 64)");
        Console.WriteLine($"sizeof PATH_INFO   = {Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>()} (want 72)");
        Console.WriteLine($"sizeof SRC_NAME    = {Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>()}");
        Console.WriteLine($"sizeof TGT_NAME    = {Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>()}");
        int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode);
        Console.WriteLine($"GetDisplayConfigBufferSizes -> {rc}  (paths {nPath}, modes {nMode})");
        if (rc != 0) return;
        var paths = new DISPLAYCONFIG_PATH_INFO[nPath];
        var modes = new DISPLAYCONFIG_MODE_INFO[nMode];
        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, 0);
        Console.WriteLine($"QueryDisplayConfig          -> {rc}");
        if (rc != 0) return;
        for (int i = 0; i < nPath; i++)
        {
            var p = paths[i];
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id } };
            int sr = DisplayConfigGetDeviceInfo(ref src);
            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME { header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id } };
            int tr = DisplayConfigGetDeviceInfo(ref tgt);
            Console.WriteLine($"path {i}: srcRC={sr} gdi='{src.viewGdiDeviceName}'  tgtRC={tr} friendly='{tgt.monitorFriendlyDeviceName}' path='{tgt.monitorDevicePath}'");
        }
    }

    // The identity chain: active path -> source GDI name (\\.\DISPLAYn) AND target device path (stable).
    private static Dictionary<string, (string path, string friendly)> BuildStableIdMap()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode) != 0)
            return map;
        var paths = new DISPLAYCONFIG_PATH_INFO[nPath];
        var modes = new DISPLAYCONFIG_MODE_INFO[nMode];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, 0) != 0)
            return map;

        for (int i = 0; i < nPath; i++)
        {
            var p = paths[i];

            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId, id = p.targetInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

            string gdi = src.viewGdiDeviceName;               // "\\.\DISPLAY1"  (shuffles)
            string path = tgt.monitorDevicePath;              // EDID-derived    (stable)
            if (!string.IsNullOrEmpty(gdi) && !string.IsNullOrEmpty(path))
                map[gdi] = (path, tgt.monitorFriendlyDeviceName ?? "");
        }
        return map;
    }

    // An order-independent key for "these exact screens" — sorted stable ids, hashed short for logs.
    private static string MonitorSetKey(List<MonitorRef> mons)
    {
        string joined = string.Join("|", mons.Select(m => m.StableId).OrderBy(s => s, StringComparer.Ordinal));
        // short, stable, deterministic (FNV-1a) — Date.now/Random-free so it's reproducible
        uint h = 2166136261;
        foreach (char c in joined) { h ^= c; h *= 16777619; }
        return $"{mons.Count}m-{h:x8}";
    }

    private static string GdiNameOf(nint hmon)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(hmon, ref mi) ? mi.szDevice : "";
    }

    // ── Window filter (mirrors VirtualDesktopController.IsCountableWindow — same "real app window" set) ──
    private static bool IsCountableWindow(nint hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOTOWNER) != hwnd) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                        or "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow");
    }

    private static string TitleOf(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ShowStateOf(uint showCmd) => showCmd switch
    {
        SW_MAXIMIZE => "Maximized",
        SW_SHOWMINIMIZED => "Minimized",
        _ => "Normal"
    };

    private static string Short(string id) => id.Length <= 24 ? id : "…" + id[^23..];

    private static void LoadKnown(Dictionary<string, Snapshot> known)
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), "ht-layout-set-*.json"))
            try { var s = SnapshotJson.Read(File.ReadAllText(f)); known[s.SetKey] = s; } catch { }
    }
    private static string SetPath(string key) => Path.Combine(Path.GetTempPath(), $"ht-layout-set-{key}.json");

    // ── Debounce + message-only window (#3) ──────────────────────────────────────────────────────────
    private static Timer? _debounce;
    private static void Debounce(int ms, Action act)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => act(), null, ms, Timeout.Infinite);
    }

    private static void RunMessageWindow(Action onDisplayChange)
    {
        WndProc proc = (h, msg, w, l) =>
        {
            if (msg == WM_DISPLAYCHANGE) onDisplayChange();
            return DefWindowProc(h, msg, w, l);
        };
        var wc = new WNDCLASS { lpfnWndProc = proc, lpszClassName = "HtLayoutSpike" };
        RegisterClass(ref wc);
        // parent = 0 → a real top-level window (created but never shown). Top-level is the requirement:
        // WM_DISPLAYCHANGE is broadcast to top-level windows and skips HWND_MESSAGE windows entirely.
        nint hwnd = CreateWindowEx(0, wc.lpszClassName, "ht-layout-spike", WS_OVERLAPPED, 0, 0, 0, 0, 0, 0, 0, 0);
        GC.KeepAlive(proc);
        while (GetMessage(out MSG m, 0, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20, GA_ROOTOWNER = 3;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
    private const uint SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_SHOWMINIMIZED = 2;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const int WS_OVERLAPPED = 0x00000000;

    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);
    private delegate bool MonitorEnumProc(nint hmon, nint hdc, nint rect, nint data);
    private delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint p);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, int flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(nint h, StringBuilder b, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint h, StringBuilder b, int max);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint h, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint h, int type, out uint x, out uint y);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint nPath, out uint nMode);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint nPath, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint nMode, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME req);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME req);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, nint h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG m);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public uint length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nint w, l; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS { public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO { public LUID adapterId; public uint id, modeInfoIdx; public uint outputTechnology, rotation, scaling; public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    // The union is 48 bytes (its largest member, DISPLAYCONFIG_TARGET_MODE), making the whole struct
    // 64 bytes — the size QueryDisplayConfig checks against. We never read the mode data, only size it:
    // get this wrong and QueryDisplayConfig returns ERROR_INVALID_PARAMETER and the stable-id chain
    // silently falls back to the shuffling GDI name.
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] payload; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
}

// ── Data model (the OS-free records the design's Hypertree.Core would hold) ──────────────────────────
internal readonly record struct Rect(int Left, int Top, int Width, int Height);
internal sealed record MonitorRef(string StableId, string Friendly, string GdiName, Rect Bounds, bool IsPrimary, uint Dpi);
internal sealed record WinPlace(long Hwnd, string MonitorStableId, string Title, Rect Normal, string Show);
internal sealed record Snapshot(string SetKey, List<MonitorRef> Monitors, List<WinPlace> Windows);

// Hand-rolled JSON (no System.Text.Json dependency to keep the spike a single obvious file).
internal static class SnapshotJson
{
    public static string Write(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("{\"setKey\":").Append(Str(s.SetKey)).Append(",\"windows\":[");
        for (int i = 0; i < s.Windows.Count; i++)
        {
            var w = s.Windows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"hwnd\":").Append(w.Hwnd)
              .Append(",\"mon\":").Append(Str(w.MonitorStableId))
              .Append(",\"title\":").Append(Str(w.Title))
              .Append(",\"l\":").Append(w.Normal.Left).Append(",\"t\":").Append(w.Normal.Top)
              .Append(",\"w\":").Append(w.Normal.Width).Append(",\"h\":").Append(w.Normal.Height)
              .Append(",\"show\":").Append(Str(w.Show)).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // Minimal reader — enough to round-trip what Write emits.
    public static Snapshot Read(string json)
    {
        string setKey = Field(json, "\"setKey\":\"");
        var wins = new List<WinPlace>();
        int i = json.IndexOf("\"windows\":[", StringComparison.Ordinal);
        if (i >= 0)
            foreach (var obj in Objects(json[(i + 10)..]))
                wins.Add(new WinPlace(
                    long.Parse(Num(obj, "\"hwnd\":"), CultureInfo.InvariantCulture),
                    Field(obj, "\"mon\":\""), Field(obj, "\"title\":\""),
                    new Rect(IntF(obj, "\"l\":"), IntF(obj, "\"t\":"), IntF(obj, "\"w\":"), IntF(obj, "\"h\":")),
                    Field(obj, "\"show\":\"")));
        return new Snapshot(setKey, new List<MonitorRef>(), wins);
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Field(string s, string key) { int i = s.IndexOf(key, StringComparison.Ordinal); if (i < 0) return ""; i += key.Length; int e = s.IndexOf('"', i); return e < 0 ? "" : s[i..e].Replace("\\\"", "\"").Replace("\\\\", "\\"); }
    private static string Num(string s, string key) { int i = s.IndexOf(key, StringComparison.Ordinal) + key.Length; int e = i; while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-')) e++; return s[i..e]; }
    private static int IntF(string s, string key) => int.Parse(Num(s, key), CultureInfo.InvariantCulture);
    private static IEnumerable<string> Objects(string s) { int d = 0, start = -1; for (int i = 0; i < s.Length; i++) { if (s[i] == '{') { if (d++ == 0) start = i; } else if (s[i] == '}') { if (--d == 0 && start >= 0) yield return s[start..(i + 1)]; } else if (s[i] == ']' && d == 0) yield break; } }
}
