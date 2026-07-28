namespace Hypertree.Platform;

/// <summary>
/// Raises OS notifications — the Windows Action Center on Windows. Resolved by the composition root so
/// no UI code references the toast interop.
/// </summary>
/// <remarks>
/// Hypertree reaches for this instead of putting a card on the overlay whenever it has something to
/// report that the user didn't stop to watch: an update check runs in the background, so its answer
/// should arrive the way any other background answer does, without covering the desktop or taking the
/// pointer. Notifications are best-effort by contract — a notification the OS drops (notifications off,
/// Focus Assist) must never fail the operation that raised it.
/// </remarks>
public interface INotifier
{
    /// <summary>
    /// Shows a notification: a bold <paramref name="title"/> over one or two lines of
    /// <paramref name="body"/>. Never throws.
    /// </summary>
    /// <param name="action">
    /// Names what clicking the notification should do, echoed back on <see cref="Activated"/>. Null
    /// makes it purely informational — a click just dismisses it.
    /// </param>
    /// <param name="silent">
    /// Suppress the notification sound. For progress notices that are followed moments later by a real
    /// result, so one operation doesn't chime twice.
    /// </param>
    /// <param name="replaces">
    /// A key naming the conversation this notification belongs to: showing another with the same key
    /// <i>replaces</i> this one rather than stacking beside it, so a multi-step operation occupies one
    /// slot that updates in place ("Checking…" → "You're up to date") instead of leaving a trail. Null
    /// stands alone.
    /// </param>
    void Show(string title, string body, string? action = null, bool silent = false, string? replaces = null);

    /// <summary>
    /// Raised when the user clicks a notification that carried an action, with that action's name. May
    /// fire on a background thread — a handler that touches UI marshals to the UI thread itself.
    /// </summary>
    event Action<string>? Activated;
}
