using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// A desktop as it appears in the map: an OS desktop id plus the label shown on its tile.
/// </summary>
public sealed record DesktopRef(DesktopId Id, string Label);

/// <summary>
/// A branch (a named stream of desktops) — a horizontal timeline in the fixed vertical stack
/// (F2). Branches keep their listed order and never reorder; the current branch sits directly below the
/// main timeline, and the stack splits around main (see <see cref="NavigationModel"/>). Entering a
/// branch resumes its <see cref="LastUsedIndex"/> rather than restarting at the first desktop.
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
}
