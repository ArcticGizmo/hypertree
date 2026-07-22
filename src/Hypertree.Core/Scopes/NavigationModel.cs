using Hypertree.Desktops;
using Hypertree.Store;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state. The <b>top row</b> is every OS desktop not assigned to a group, in natural
/// order (rebuilt from the controller). <b>Groups</b> hang below in a fixed vertical stack — they do
/// NOT reorder as you navigate:
///   • Down steps one level deeper (top → group 0 → group 1 → …); Up steps back toward the top.
///   • Left/Right moves within the current row (top desktops, or the current group's desktops).
///   • The stack is only reordered when the map (re)opens — the last-used group moves to the top
///     (nearest), so you re-enter where you left off (<see cref="PrepareForMapOpen"/>).
/// Holds no Win32/UI, so the whole feel is unit-testable against a fake controller.
/// </summary>
public sealed class NavigationModel
{
    private readonly IDesktopController _desktops;
    private readonly IStateStore? _store;
    private readonly List<Group> _groups = new();

    private List<DesktopRef> _topRow = new();
    private int _level;         // 0 = top row; 1..n = _groups[_level - 1]
    private int _topIndex;      // cursor within the top row
    private int _lastGroup;     // index of the most-recently-used group (for reorder-on-open)
    private DesktopId _target;  // desktop the model last switched to

    public event Action? Changed;

    public NavigationModel(IDesktopController desktops, IStateStore? store = null)
    {
        _desktops = desktops;
        _store = store;

        RestoreGroups();
        SyncTopRow();
        int idx = _topRow.FindIndex(d => d.Id == desktops.Current);
        if (idx >= 0) _topIndex = idx;
        _target = CurrentDesktop().Id;
    }

    // ── Queries ──────────────────────────────────────────────────────────────────

    public bool OnTop => _level == 0;
    public int GroupCount => _groups.Count;
    public int CurrentTopIndex => _topIndex;

    /// <summary>The (group, desktop) currently selected, or null when on the top row.</summary>
    public (int group, int desktop)? CurrentGroupDesktop
        => _level == 0 ? null : (_level - 1, _groups[_level - 1].LastUsedIndex);

    /// <summary>A top-row desktop id to use as a fallback when tearing a group's desktops down.</summary>
    public DesktopId FallbackDesktopId =>
        _topRow.Count > 0 ? _topRow[Math.Clamp(_topIndex, 0, _topRow.Count - 1)].Id : _desktops.Current;

    public IEnumerable<DesktopId> GroupDesktopIds() => _groups.SelectMany(g => g.Desktops).Select(d => d.Id);

    private DesktopRef CurrentDesktop()
        => _level == 0
            ? _topRow[Math.Clamp(_topIndex, 0, Math.Max(0, _topRow.Count - 1))]
            : _groups[_level - 1].Desktops[_groups[_level - 1].LastUsedIndex];

    /// <summary>Render-ready snapshot: top row + groups in their fixed stack order (no rotation).</summary>
    public NavMap BuildMap()
    {
        var top = new List<NavMapTile>(_topRow.Count);
        for (int i = 0; i < _topRow.Count; i++)
            top.Add(new NavMapTile(_topRow[i].Label, _level == 0 && i == _topIndex));

        var groups = new List<NavMapGroup>(_groups.Count);
        for (int gi = 0; gi < _groups.Count; gi++)
        {
            Group g = _groups[gi];
            bool currentLevel = _level == gi + 1;
            var tiles = new List<NavMapTile>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
                tiles.Add(new NavMapTile(g.Desktops[j].Label, currentLevel && j == g.LastUsedIndex));
            groups.Add(new NavMapGroup(gi, g.Name, tiles, currentLevel, g.LastUsedIndex));
        }

        return new NavMap(top, _topRow.Count == 0 ? 0 : _topIndex, _level == 0, groups);
    }

    // ── Navigation (fixed ladder) ─────────────────────────────────────────────────

    public bool Apply(NavAction action) => action switch
    {
        NavAction.MoveLeft => Move(-1),
        NavAction.MoveRight => Move(+1),
        NavAction.Dive => Down(),
        NavAction.Surface => Up(),
        _ => false,
    };

    private bool Move(int delta)
    {
        if (_level == 0)
        {
            if (_topRow.Count == 0) return false;
            int next = Math.Clamp(_topIndex + delta, 0, _topRow.Count - 1);
            if (next == _topIndex) return false;
            _topIndex = next;
        }
        else
        {
            Group g = _groups[_level - 1];
            int next = Math.Clamp(g.LastUsedIndex + delta, 0, g.Desktops.Count - 1);
            if (next == g.LastUsedIndex) return false;
            g.LastUsedIndex = next;
        }
        return Commit();
    }

    private bool Down()
    {
        if (_level >= _groups.Count) return false; // at the bottom of the stack — no wrap
        _level++;
        _lastGroup = _level - 1;
        return Commit();
    }

    private bool Up()
    {
        if (_level == 0) return false;
        _level--;
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific top-row desktop.</summary>
    public bool GoToTop(int index)
    {
        if (index < 0 || index >= _topRow.Count) return false;
        _level = 0;
        _topIndex = index;
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific desktop within a specific group.</summary>
    public bool GoToGroupDesktop(int groupIndex, int desktopIndex)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count) return false;
        Group g = _groups[groupIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Desktops.Count) return false;
        _level = groupIndex + 1;
        _lastGroup = groupIndex;
        g.LastUsedIndex = desktopIndex;
        return Commit();
    }

    private bool Commit()
    {
        DesktopId id = CurrentDesktop().Id;
        if (id == _target) return false;
        _target = id;
        _desktops.SwitchTo(id);
        Save();
        Changed?.Invoke();
        return true;
    }

    // ── Map open: put the last-used group on top (nearest), without disturbing position ──
    public void PrepareForMapOpen()
    {
        if (_groups.Count <= 1) return;
        int sel = Math.Clamp(_lastGroup, 0, _groups.Count - 1);
        if (sel == 0) return;

        Group? current = _level >= 1 ? _groups[_level - 1] : null;
        Group g = _groups[sel];
        _groups.RemoveAt(sel);
        _groups.Insert(0, g);
        _lastGroup = 0;
        if (current is not null) _level = _groups.IndexOf(current) + 1; // keep pointing at the same group
        Save();
        Changed?.Invoke();
    }

    // ── Group management ─────────────────────────────────────────────────────────

    public void SyncTopRow()
    {
        var grouped = _groups.SelectMany(g => g.Desktops).Select(d => d.Id.Value).ToHashSet();
        _topRow = _desktops.List()
            .Where(d => !grouped.Contains(d.Id.Value))
            .Select(d => new DesktopRef(d.Id, string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name))
            .ToList();

        _level = Math.Clamp(_level, 0, _groups.Count);
        _topIndex = _topRow.Count == 0 ? 0 : Math.Clamp(_topIndex, 0, _topRow.Count - 1);
        _lastGroup = _groups.Count == 0 ? 0 : Math.Clamp(_lastGroup, 0, _groups.Count - 1);
    }

    /// <summary>Add a group at the top of the stack (nearest) and mark it last-used. Keeps position.</summary>
    public void AddGroup(Group group)
    {
        _groups.Insert(0, group);
        if (_level >= 1) _level++;   // existing selection shifts down one
        _lastGroup = 0;
        SyncTopRow();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Remove the group at <paramref name="index"/> and return it (for desktop teardown).</summary>
    public Group? RemoveGroup(int index)
    {
        if (index < 0 || index >= _groups.Count) return null;
        Group removed = _groups[index];
        _groups.RemoveAt(index);
        AdjustLevelForRemoval(index);
        SyncTopRow();
        Save();
        Changed?.Invoke();
        return removed;
    }

    // ── Single-desktop deletion (× badge / delete button) ─────────────────────────

    public int TotalDesktops => _topRow.Count + _groups.Sum(g => g.Count);

    public DesktopId? TopDesktopId(int index)
        => index >= 0 && index < _topRow.Count ? _topRow[index].Id : null;

    public (DesktopId id, string label)? PeekTopDesktop(int index)
        => index >= 0 && index < _topRow.Count ? (_topRow[index].Id, _topRow[index].Label) : null;

    public (DesktopId id, string label)? PeekGroupDesktop(int groupIndex, int desktopIndex)
        => groupIndex >= 0 && groupIndex < _groups.Count
           && desktopIndex >= 0 && desktopIndex < _groups[groupIndex].Count
            ? (_groups[groupIndex].Desktops[desktopIndex].Id, _groups[groupIndex].Desktops[desktopIndex].Label)
            : null;

    /// <summary>
    /// Detach a desktop from a group so the caller can destroy it. Returns the id (null if invalid).
    /// If it was the group's last desktop, the whole group is removed. Pure mutation — the caller
    /// destroys the OS desktop then calls <see cref="Resync"/>.
    /// </summary>
    public DesktopId? DetachGroupDesktop(int groupIndex, int desktopIndex)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count) return null;
        Group g = _groups[groupIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Count) return null;

        DesktopId id = g.Desktops[desktopIndex].Id;
        if (g.Count == 1)
        {
            _groups.RemoveAt(groupIndex);
            AdjustLevelForRemoval(groupIndex);
        }
        else
        {
            g.RemoveDesktopAt(desktopIndex);
        }
        return id;
    }

    private void AdjustLevelForRemoval(int removedGroupIndex)
    {
        if (_level - 1 == removedGroupIndex) _level = 0;        // we were in it → back to top
        else if (_level - 1 > removedGroupIndex) _level--;      // shift up
        _level = Math.Clamp(_level, 0, _groups.Count);
        _lastGroup = _groups.Count == 0 ? 0 : Math.Clamp(_lastGroup, 0, _groups.Count - 1);
    }

    /// <summary>Re-anchor to whatever desktop the OS is now showing (after a create/destroy).</summary>
    public void Resync()
    {
        SyncTopRow();
        DesktopId cur = _desktops.Current;

        int ti = _topRow.FindIndex(d => d.Id == cur);
        if (ti >= 0) { _level = 0; _topIndex = ti; }
        else
        {
            for (int gi = 0; gi < _groups.Count; gi++)
            {
                int di = -1;
                for (int j = 0; j < _groups[gi].Desktops.Count; j++) if (_groups[gi].Desktops[j].Id == cur) di = j;
                if (di >= 0) { _level = gi + 1; _lastGroup = gi; _groups[gi].LastUsedIndex = di; break; }
            }
        }

        _target = CurrentDesktop().Id;
        Save();
        Changed?.Invoke();
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void RestoreGroups()
    {
        if (_store is null) return;
        PersistedState state = _store.Load();
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        foreach (PersistedGroup pg in state.Groups)
        {
            var desks = pg.Desktops
                .Where(d => live.Contains(d.Id))
                .Select(d => new DesktopRef(new DesktopId(d.Id), d.Label))
                .ToList();
            if (desks.Count > 0) _groups.Add(new Group(pg.Name, desks, pg.LastUsedIndex));
        }
        _lastGroup = _groups.Count == 0 ? 0 : Math.Clamp(state.ActiveGroup, 0, _groups.Count - 1);
    }

    private void Save()
    {
        _store?.Save(new PersistedState
        {
            ActiveGroup = _lastGroup,
            Groups = _groups.Select(g => new PersistedGroup
            {
                Name = g.Name,
                LastUsedIndex = g.LastUsedIndex,
                Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
            }).ToList(),
        });
    }
}
