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
/// The trigger key of a hotkey. Only the keys Hypertree binds today — the four arrows (the depth
/// and within-level axes). Kept as an enum, not a raw VK, so Core stays OS-agnostic; the Windows
/// layer maps these to virtual-key codes.
/// </summary>
public enum HotkeyKey
{
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Space,
}
