using System.Runtime.InteropServices;
using Hypertree.Platform;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IGlobalHotkey"/> via <c>RegisterHotKey</c>/<c>WM_HOTKEY</c>. Registering with
/// a null window handle posts <c>WM_HOTKEY</c> to the calling thread's queue, so this owns a
/// dedicated background thread that registers the combo and runs a <c>GetMessage</c> loop — no
/// dependency on the UI toolkit's pump. The press callback fires on that thread; the caller
/// marshals to its UI thread. Dispose posts <c>WM_QUIT</c>, which unregisters the hotkey.
///
/// (Pattern lifted from perch's GlobalHotkey; extended with MOD_WIN and arrow virtual-keys. Per M0,
/// Ctrl+Alt+Arrow is the default layer — Win+Ctrl+Arrow is reserved by the native desktop switch.)
/// </summary>
public sealed class GlobalHotkey : IGlobalHotkey
{
    private const int  WM_HOTKEY    = 0x0312;
    private const uint WM_QUIT      = 0x0012;
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int  HotkeyId     = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd; public uint message; public nint wParam; public nint lParam;
        public uint time; public int ptX; public int ptY;
    }

    private Thread? _thread;
    private uint _threadId;
    private Action? _onPressed;
    private volatile bool _disposed;

    public bool Register(HotkeyModifiers modifiers, HotkeyKey key, Action onPressed)
    {
        if (_thread != null) throw new InvalidOperationException("This hotkey is already registered.");

        _onPressed = onPressed;
        uint vk = VirtualKey(key);
        uint mods = MOD_NOREPEAT
            | ((modifiers & HotkeyModifiers.Alt)     != 0 ? MOD_ALT     : 0)
            | ((modifiers & HotkeyModifiers.Control) != 0 ? MOD_CONTROL : 0)
            | ((modifiers & HotkeyModifiers.Shift)   != 0 ? MOD_SHIFT   : 0)
            | ((modifiers & HotkeyModifiers.Win)     != 0 ? MOD_WIN     : 0);

        using var ready = new ManualResetEventSlim();
        bool registered = false;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            registered = RegisterHotKey(0, HotkeyId, mods, vk);
            ready.Set();
            if (!registered) return;

            while (!_disposed && GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                    _onPressed?.Invoke();
            }

            UnregisterHotKey(0, HotkeyId);
        })
        {
            IsBackground = true,
            Name = "HypertreeGlobalHotkey",
        };
        _thread.Start();
        ready.Wait();
        return registered;
    }

    private static uint VirtualKey(HotkeyKey key) => key switch
    {
        HotkeyKey.ArrowLeft  => 0x25,
        HotkeyKey.ArrowUp    => 0x26,
        HotkeyKey.ArrowRight => 0x27,
        HotkeyKey.ArrowDown  => 0x28,
        HotkeyKey.Space      => 0x20,
        HotkeyKey.P          => 0x50,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread?.Join(500);
    }
}
