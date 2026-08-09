using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The "pull windows" flow — the mirror image of <see cref="MoveContent"/>. Where move relocates <em>this</em>
/// desktop's windows <em>elsewhere</em> (and so needs a second phase to pick the destination), pull brings
/// windows from <em>elsewhere</em> onto the desktop you're already on. The destination is fixed — it's the
/// current desktop — so there is no phase 2: it's just the shared <see cref="WindowPickerContent"/> grid, and
/// Enter drops the ticked windows here.
///
/// Its cards span desktops, so each caption names its source desktop (<see cref="ShowSource"/>). Like
/// <see cref="MoveContent"/> it holds no model: the pull is raised as an event for <c>App</c>.
/// </summary>
internal sealed class PullContent : WindowPickerContent
{
    /// <summary>Enter — App moves these windows onto the current desktop.</summary>
    public event Action<IReadOnlyList<nint>>? PullRequested;

    public PullContent(WindowMoveSession session, double initialZoom = 1.0) : base(session, initialZoom) { }

    protected override string PickerHint => "←→↑↓ move · Space tick · Enter pull here · Esc cancel";
    protected override string EmptyHint => "No windows on other desktops to pull · Esc to close";
    protected override bool ShowSource => true;

    protected override void ConfirmSelection()
    {
        PullRequested?.Invoke(Session.SelectedHwnds);
        Stage?.CompleteToBase(); // unwind to the map if we opened over it, else dismiss to the desktop
    }
}
