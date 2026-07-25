namespace Hypertree.Platform;

/// <summary>
/// Forces a window to the foreground and hands it keyboard focus. A tray/hotkey process is a
/// <em>background</em> process to Windows, which blocks it from calling <c>SetForegroundWindow</c>
/// outright — so a window it summons (the spotlight / command palette) can't take typing on its own.
/// The Windows impl does the <c>AttachThreadInput</c> + <c>SetForegroundWindow</c>/<c>SetFocus</c>
/// dance to steal focus anyway (lifted from perch's <c>WindowChrome.ForceForeground</c>).
///
/// This is the <b>opposite</b> of the HUD/board policy, which uses <c>WS_EX_NOACTIVATE</c> to avoid
/// ever taking focus — hence a separate primitive, applied only to the palette windows.
/// </summary>
public interface IForegroundActivator
{
    /// <summary>Force <paramref name="hwnd"/> to the foreground and give it focus. Best-effort; a
    /// zero handle is ignored.</summary>
    void ForceForeground(nint hwnd);
}
