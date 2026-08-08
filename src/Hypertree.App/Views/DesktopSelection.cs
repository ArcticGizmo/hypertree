namespace Hypertree.App.Views;

/// <summary>Which desktop an action targets: a main-timeline desktop (<paramref name="OnMain"/> true,
/// <paramref name="DesktopIndex"/> = its top-row index) or a branch desktop (<paramref name="BranchIndex"/>
/// + <paramref name="DesktopIndex"/>). The bridge between the spatial map's <c>DesktopId</c> cursor and the
/// position-based desktop operations on <c>NavigationModel</c> (rename, new, delete).</summary>
internal readonly record struct DesktopSelection(bool OnMain, int BranchIndex, int DesktopIndex);
