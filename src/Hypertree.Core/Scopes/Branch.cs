using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// A desktop as it appears in the map: an OS desktop id plus the label shown on its tile.
/// </summary>
public sealed record DesktopRef(DesktopId Id, string Label);

/// <summary>
/// A branch (a named stream of desktops) — a horizontal timeline in the vertical stack (F2). Branches
/// hold their listed order as you navigate (nothing rotates under the cursor); only an explicit reorder
/// from the map moves one (<see cref="NavigationModel.MoveBranchToRow"/>). The stack splits around the
/// main timeline (see <see cref="NavigationModel"/>). Entering a branch resumes its
/// <see cref="LastUsedIndex"/> rather than restarting at the first desktop.
/// </summary>
public sealed class Branch
{
    private readonly List<DesktopRef> _desktops;

    /// <summary>
    /// Stable identity, minted at creation and persisted. Nothing else about a branch is safe to address
    /// it by from outside: <see cref="Name"/> isn't unique or enforced, and the list index shifts under
    /// <see cref="NavigationModel.AddBranchBelow"/> / <see cref="NavigationModel.MoveBranchToRow"/> — so a
    /// caller that read the list, then acted on an index, could land on a branch the user has since moved.
    /// This is what the status file publishes and what <c>htree goto --id</c> resolves.
    /// </summary>
    public Guid Id { get; }

    public string Name { get; private set; }
    public IReadOnlyList<DesktopRef> Desktops => _desktops;

    /// <summary>Index within <see cref="Desktops"/> last occupied — the resume point. Always valid.</summary>
    public int LastUsedIndex { get; set; }

    /// <param name="id">Restores a persisted identity. Omit (or pass <see cref="Guid.Empty"/>) for a branch
    /// being created now, or one restored from a snapshot — a snapshot is a template, so each restore is a
    /// genuinely new branch and mints a new id.</param>
    public Branch(string name, IReadOnlyList<DesktopRef> desktops, int lastUsedIndex = 0, Guid id = default)
    {
        if (desktops.Count == 0) throw new ArgumentException("A branch needs at least one desktop.", nameof(desktops));
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        _desktops = desktops.ToList();
        LastUsedIndex = Math.Clamp(lastUsedIndex, 0, _desktops.Count - 1);
    }

    public int Count => _desktops.Count;

    /// <summary>Remove a desktop from the branch, keeping <see cref="LastUsedIndex"/> valid.</summary>
    public void RemoveDesktopAt(int index)
    {
        _desktops.RemoveAt(index);
        if (_desktops.Count > 0) LastUsedIndex = Math.Clamp(LastUsedIndex, 0, _desktops.Count - 1);
    }

    /// <summary>
    /// Insert a desktop at <paramref name="index"/> (clamped) — a desktop dragged in from main or from
    /// another branch. The resume point stays on whichever desktop it was already on.
    /// </summary>
    public void InsertDesktop(int index, DesktopRef desktop)
    {
        DesktopRef resume = _desktops[LastUsedIndex];
        _desktops.Insert(Math.Clamp(index, 0, _desktops.Count), desktop);
        LastUsedIndex = _desktops.IndexOf(resume);
    }

    /// <summary>
    /// Reorder a desktop within the branch. <paramref name="insertAt"/> is an <em>insertion point</em> in
    /// the current list (0..Count, counting the desktop itself) — the same thing the map's drop caret
    /// points at — so dragging right past one neighbour moves it one place right. The resume point stays
    /// on the desktop it was on. False when the move resolves to a no-op.
    /// </summary>
    public bool MoveDesktop(int from, int insertAt)
    {
        if (from < 0 || from >= _desktops.Count) return false;
        int to = Math.Clamp(insertAt > from ? insertAt - 1 : insertAt, 0, _desktops.Count - 1);
        if (to == from) return false;

        DesktopRef resume = _desktops[LastUsedIndex], moved = _desktops[from];
        _desktops.RemoveAt(from);
        _desktops.Insert(to, moved);
        LastUsedIndex = _desktops.IndexOf(resume);
        return true;
    }

    /// <summary>Rename the branch itself. The name isn't unique or enforced (see <see cref="Id"/>).</summary>
    public void SetName(string name) => Name = name;

    /// <summary>Rename a desktop in place, keeping its position and the resume point.</summary>
    public void SetLabel(int index, string label)
    {
        if (index < 0 || index >= _desktops.Count) return;
        _desktops[index] = _desktops[index] with { Label = label };
    }
}
