namespace Hypertree.Desktops;

/// <summary>
/// A top-level application window as it appears in the "move windows" / "pull windows" picker: the OS
/// window handle (the key we move by — see <see cref="IDesktopController.MoveWindowToDesktop"/>), plus a
/// title and owning process name for the card caption. Best-effort text — an unreadable title/process
/// falls back to an empty string rather than throwing.
/// <para>
/// <see cref="DesktopName"/> names the desktop the window currently sits on. It's empty for the move
/// picker (every card is on the origin, so there's nothing to disambiguate) and populated for the pull
/// picker (cards come from many desktops, so the caption says which one each is on).
/// </para>
/// </summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName, string DesktopName = "");
