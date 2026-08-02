namespace Hypertree.Desktops;

/// <summary>
/// A top-level application window as it appears in the "move windows" picker: the OS window handle
/// (the key we move by — see <see cref="IDesktopController.MoveWindowToDesktop"/>), plus a title and
/// owning process name for the card caption, the process's full executable path — the key
/// <see cref="Launch.SessionCapture"/> relaunches by — and the 1-based index of the monitor the window sits
/// on (0 = unknown / not computed), which a recipe records so restore can put the window back on the same
/// screen. Best-effort text — an unreadable title / process / path falls back to an empty string rather
/// than throwing (a window we can't resolve a path for simply isn't capturable, and drops out of a session).
/// </summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName, string ExecutablePath = "", int Monitor = 0);
