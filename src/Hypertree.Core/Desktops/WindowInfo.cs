namespace Hypertree.Desktops;

/// <summary>
/// A top-level application window as it appears in the "move windows" picker: the OS window handle
/// (the key we move by — see <see cref="IDesktopController.MoveWindowToDesktop"/>), plus a title and
/// owning process name for the card caption. Best-effort text — an unreadable title/process falls
/// back to an empty string rather than throwing.
/// </summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName);
