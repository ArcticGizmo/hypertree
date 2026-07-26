namespace Hypertree.Platform;

/// <summary>
/// A process-wide keyboard shortcut, registered with the OS so it fires even when Hypertree has
/// no focused window. On Windows it's <c>RegisterHotKey</c>/<c>WM_HOTKEY</c> on a dedicated
/// message-loop thread (see docs/design/m0-findings.md — Ctrl+Alt+Arrow is the default layer;
/// Win+Ctrl+Arrow is reserved by the native desktop switch). Resolved by the composition root so
/// no UI code hard-codes the interop. Dispose to unregister.
/// </summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>
    /// Registers <paramref name="modifiers"/> + <paramref name="key"/> and invokes
    /// <paramref name="onPressed"/> whenever it fires. The callback runs on an arbitrary thread —
    /// the caller marshals to its UI thread. Returns false if the OS refused the binding (another
    /// app owns it); a refusal is safe to ignore. Call once per instance.
    /// </summary>
    bool Register(HotkeyModifiers modifiers, HotkeyKey key, Action onPressed);
}

/// <summary>Modifier keys held for a hotkey to fire. Includes Win — Hypertree offers Win-layer chords.</summary>
[Flags]
public enum HotkeyModifiers
{
    None    = 0,
    Alt     = 1,
    Control = 2,
    Shift   = 4,
    Win     = 8,
}

/// <summary>
/// The trigger key of a hotkey. Covers the rebinding surface Hypertree offers — the four arrows (the
/// depth and within-level axes), Space, the letters, the top-row digits and the function keys. Kept as
/// an enum, not a raw VK, so Core stays OS-agnostic; the Windows layer maps these to virtual-key codes.
/// The member order matters: the letter / digit / function-key ranges are contiguous so the platform
/// layer can map them arithmetically. Don't reorder without updating <c>GlobalHotkey.VirtualKey</c>.
/// </summary>
public enum HotkeyKey
{
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Space,

    // Letters A–Z (contiguous).
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Top-row digits 0–9 (contiguous).
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    // Function keys F1–F12 (contiguous).
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}
