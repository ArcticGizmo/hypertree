namespace Hypertree.Scopes;

/// <summary>
/// A desktop's slot in the vertical model: on the main timeline (<see cref="OnMain"/> true, with
/// <see cref="BranchIndex"/> unused — conventionally -1) at <see cref="DesktopIndex"/>, or inside the branch
/// at <see cref="BranchIndex"/> at that index.
/// </summary>
/// <remarks>
/// Serves three roles that share this exact shape: the position <see cref="NavigationModel.Locate"/> returns,
/// and the <c>from</c> source and <c>to</c> destination of <see cref="NavigationModel.MoveDesktop"/> (for a
/// destination, <see cref="DesktopIndex"/> is the insertion point). Replaced the repeated
/// <c>(bool, int, int)</c> tuple and <see cref="NavigationModel.MoveDesktop"/>'s six-positional-argument
/// signature, where a stray transposed bool/int was invisible at the call site.
/// </remarks>
public readonly record struct DesktopAddress(bool OnMain, int BranchIndex, int DesktopIndex);
