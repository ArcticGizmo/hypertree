using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state. The <b>top row</b> is every OS desktop not assigned to a group, in natural
/// order (rebuilt from the controller). <b>Groups</b> hang below as a wrapping carousel:
///   • Down from the top row dives into the active group (nearest the top), resuming its last-used desktop.
///   • Down again rotates to the next group (it becomes active/nearest; the one you left wraps to the bottom).
///   • Up surfaces straight back to the top row from any group.
///   • Left/Right moves within the current row (clamped).
/// Holds no Win32/UI, so the whole feel is unit-testable against a fake controller.
/// </summary>
public sealed class NavigationModel
{
    private readonly IDesktopController _desktops;
    private readonly List<Group> _groups = new();

    private List<DesktopRef> _topRow = new();
    private bool _onTop = true;
    private int _topIndex;      // position within the top row
    private int _activeGroup;   // index of the active (nearest) group
    private DesktopId _target;  // desktop the model last switched to

    public event Action? Changed;

    public NavigationModel(IDesktopController desktops)
    {
        _desktops = desktops;
        SyncTopRow();
        // Start on whichever top-row desktop the OS is showing, if any.
        int idx = _topRow.FindIndex(d => d.Id == desktops.Current);
        if (idx >= 0) _topIndex = idx;
        _target = CurrentDesktop().Id;
    }

    // ── Queries ──────────────────────────────────────────────────────────────────

    public bool OnTop => _onTop;
    public int GroupCount => _groups.Count;
    public int ActiveGroupIndex => _activeGroup;

    /// <summary>A top-row desktop id to use as a fallback when tearing a group's desktops down.</summary>
    public DesktopId FallbackDesktopId =>
        _topRow.Count > 0 ? _topRow[Math.Clamp(_topIndex, 0, _topRow.Count - 1)].Id : _desktops.Current;

    private DesktopRef CurrentDesktop()
        => _onTop
            ? _topRow[Math.Clamp(_topIndex, 0, Math.Max(0, _topRow.Count - 1))]
            : _groups[_activeGroup].Desktops[_groups[_activeGroup].LastUsedIndex];

    /// <summary>Render-ready snapshot: top row + groups in carousel order (active first).</summary>
    public NavMap BuildMap()
    {
        var top = new List<NavMapTile>(_topRow.Count);
        for (int i = 0; i < _topRow.Count; i++)
            top.Add(new NavMapTile(_topRow[i].Label, _onTop && i == _topIndex));

        var groups = new List<NavMapGroup>(_groups.Count);
        for (int k = 0; k < _groups.Count; k++)
        {
            int gi = (_activeGroup + k) % _groups.Count; // carousel: active first, then wrap
            Group g = _groups[gi];
            bool currentLevel = !_onTop && gi == _activeGroup;
            var tiles = new List<NavMapTile>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
                tiles.Add(new NavMapTile(g.Desktops[j].Label, currentLevel && j == g.LastUsedIndex));
            groups.Add(new NavMapGroup(gi, g.Name, tiles, currentLevel));
        }

        return new NavMap(top, _onTop, groups);
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    public bool Apply(NavAction action) => action switch
    {
        NavAction.MoveLeft => Move(-1),
        NavAction.MoveRight => Move(+1),
        NavAction.Dive => Dive(),
        NavAction.Surface => Surface(),
        _ => false,
    };

    private bool Move(int delta)
    {
        if (_onTop)
        {
            if (_topRow.Count == 0) return false;
            int next = Math.Clamp(_topIndex + delta, 0, _topRow.Count - 1);
            if (next == _topIndex) return false;
            _topIndex = next;
        }
        else
        {
            Group g = _groups[_activeGroup];
            int next = Math.Clamp(g.LastUsedIndex + delta, 0, g.Desktops.Count - 1);
            if (next == g.LastUsedIndex) return false;
            g.LastUsedIndex = next;
        }
        return Commit();
    }

    private bool Dive()
    {
        if (_groups.Count == 0) return false;
        if (_onTop)
        {
            _onTop = false; // enter the active group, resuming its last-used desktop
        }
        else
        {
            if (_groups.Count <= 1) return false;              // nothing to rotate to
            _activeGroup = (_activeGroup + 1) % _groups.Count; // next group becomes active/nearest
        }
        return Commit();
    }

    private bool Surface()
    {
        if (_onTop) return false;
        _onTop = true; // straight back to the top row (to the desktop we left)
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific top-row desktop.</summary>
    public bool GoToTop(int index)
    {
        if (index < 0 || index >= _topRow.Count) return false;
        _onTop = true;
        _topIndex = index;
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific desktop within a specific group.</summary>
    public bool GoToGroupDesktop(int groupIndex, int desktopIndex)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count) return false;
        Group g = _groups[groupIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Desktops.Count) return false;
        _onTop = false;
        _activeGroup = groupIndex;
        g.LastUsedIndex = desktopIndex;
        return Commit();
    }

    private bool Commit()
    {
        DesktopId id = CurrentDesktop().Id;
        if (id == _target) return false;
        _target = id;
        _desktops.SwitchTo(id);
        Changed?.Invoke();
        return true;
    }

    // ── Group management ─────────────────────────────────────────────────────────

    /// <summary>Rebuild the top row from the OS: every desktop not in a group, in natural order.</summary>
    public void SyncTopRow()
    {
        var grouped = _groups.SelectMany(g => g.Desktops).Select(d => d.Id.Value).ToHashSet();
        _topRow = _desktops.List()
            .Where(d => !grouped.Contains(d.Id.Value))
            .Select(d => new DesktopRef(d.Id, string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name))
            .ToList();

        if (_groups.Count == 0) _onTop = true;
        _activeGroup = _groups.Count == 0 ? 0 : Math.Clamp(_activeGroup, 0, _groups.Count - 1);
        _topIndex = _topRow.Count == 0 ? 0 : Math.Clamp(_topIndex, 0, _topRow.Count - 1);
    }

    /// <summary>Add a group and make it the active (nearest) one. Refreshes the top row.</summary>
    public void AddGroup(Group group)
    {
        _groups.Add(group);
        _activeGroup = _groups.Count - 1;
        SyncTopRow();
        Changed?.Invoke();
    }

    /// <summary>Remove the group at <paramref name="index"/> and return it (for desktop teardown).</summary>
    public Group? RemoveGroup(int index)
    {
        if (index < 0 || index >= _groups.Count) return null;
        Group removed = _groups[index];
        _groups.RemoveAt(index);
        if (_groups.Count == 0) _onTop = true;
        _activeGroup = _groups.Count == 0 ? 0 : Math.Clamp(_activeGroup, 0, _groups.Count - 1);
        SyncTopRow();
        Changed?.Invoke();
        return removed;
    }
}
