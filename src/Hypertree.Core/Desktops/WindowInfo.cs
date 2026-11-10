namespace Hypertree.Desktops;

/// <summary>
/// A top-level application window as it appears in the window pickers and the window finder: the OS
/// window handle (the key we move/focus by — see <see cref="IDesktopController.MoveWindowToDesktop"/>), plus a
/// title and owning process name for the caption. Best-effort text — an unreadable title/process
/// falls back to an empty string rather than throwing.
/// <para>
/// <see cref="DesktopName"/> names the desktop the window currently sits on. It's empty for the move
/// picker (every card is on the origin, so there's nothing to disambiguate) and populated for the pull
/// picker (cards come from many desktops, so the caption says which one each is on).
/// </para>
/// <para>
/// <see cref="Desktop"/> is the id of that desktop, populated only by <see cref="IDesktopController.AllWindows"/>
/// — the window finder needs it to switch there before focusing the window.
/// </para>
/// </summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName, string DesktopName = "",
                                DesktopId? Desktop = null);
