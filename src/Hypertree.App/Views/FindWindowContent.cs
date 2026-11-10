using Hypertree.Desktops;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The window finder (<c>f</c> / <c>Ctrl+F</c> on the map): the same live-thumbnail grid as move and pull, so
/// you pick a window by what it looks like rather than by a title that ten terminals share. It spans every
/// desktop, so each caption names where the window lives (<see cref="ShowSource"/>). Single-choice — Enter or
/// a click raises <see cref="WindowChosen"/>, and <c>App</c> switches to the window's desktop and focuses it.
/// </summary>
internal sealed class FindWindowContent : WindowPickerContent
{
    /// <summary>Enter / click — App switches to this window's desktop, focuses it and dismisses the stage.</summary>
    public event Action<WindowInfo>? WindowChosen;

    public FindWindowContent(WindowMoveSession session, double initialZoom = 1.0) : base(session, initialZoom) { }

    protected override string PickerHint => "←→↑↓ move · Enter go to window · Esc back";
    protected override string EmptyHint => "No windows open · Esc to close";
    protected override bool ShowSource => true;
    protected override bool MultiSelect => false;

    protected override void ConfirmSelection()
    {
        if (Session.Focused is { } w) WindowChosen?.Invoke(w);
    }
}
