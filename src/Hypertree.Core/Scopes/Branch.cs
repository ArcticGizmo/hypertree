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

    public string Name { get; }
    public IReadOnlyList<DesktopRef> Desktops => _desktops;

    /// <summary>Index within <see cref="Desktops"/> last occupied — the resume point. Always valid.</summary>
    public int LastUsedIndex { get; set; }

    public Branch(string name, IReadOnlyList<DesktopRef> desktops, int lastUsedIndex = 0)
    {
        if (desktops.Count == 0) throw new ArgumentException("A branch needs at least one desktop.", nameof(desktops));
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

    /// <summary>Rename a desktop in place, keeping its position and the resume point.</summary>
    public void SetLabel(int index, string label)
    {
        if (index < 0 || index >= _desktops.Count) return;
        _desktops[index] = _desktops[index] with { Label = label };
    }
}
