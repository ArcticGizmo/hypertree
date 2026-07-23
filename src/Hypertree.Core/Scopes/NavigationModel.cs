using Hypertree.Desktops;
using Hypertree.Store;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state, vertical model (F2 — "main above current"). The <b>main timeline</b>
/// (<see cref="_topRow"/>) is every OS desktop not assigned to a group, in natural order; it is the
/// pivot. <b>Groups</b> are a fixed vertical stack that never reorders — the current group sits
/// directly <em>below</em> the main timeline, groups listed before it stack above main (in order),
/// groups listed after it stack below the current group. So for groups [A,B,C] with current B, the
/// vertical sequence is: A / MAIN / B / C.
///
/// Navigation:
///   • <b>Up</b>: inside a group → the main timeline; on main → the previous group (currentGroup−1),
///     entering it. No-op past the first group.
///   • <b>Down</b>: on main → re-enter the current group; inside a group → the next group
///     (currentGroup+1). No-op past the last group.
///   • <b>Left/Right</b>: within the current row (main desktops, or the current group's desktops).
/// The accepted asymmetry: Up from B passes through MAIN before reaching A, but Down from B goes
/// straight to C (main is always directly above the current group, so it isn't re-crossed going down).
/// Ends clamp (no wrap). Holds no Win32/UI, so the whole feel is unit-testable against a fake controller.
/// </summary>
public sealed class NavigationModel
{
    private readonly IDesktopController _desktops;
    private readonly IStateStore? _store;
    private readonly List<Group> _groups = new();

    private List<DesktopRef> _topRow = new();
    private bool _onMain = true;    // true = on the main timeline; false = inside _groups[_currentGroup]
    private int _currentGroup;      // the group directly below main (the Down/Up pivot). Always clamped valid.
    private int _topIndex;          // cursor within the main timeline
    private DesktopId _target;      // desktop the model last switched to

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

    public bool OnTop => _onMain;
    public int GroupCount => _groups.Count;
    public int CurrentTopIndex => _topIndex;

    /// <summary>The (group, desktop) currently selected, or null when on the main timeline.</summary>
    public (int group, int desktop)? CurrentGroupDesktop
        => _onMain || _groups.Count == 0 ? null : (_currentGroup, _groups[_currentGroup].LastUsedIndex);

    /// <summary>The pivot group index (the group directly below main), or -1 when there are no groups.
    /// Valid even on the main timeline — that's the group Down would re-enter.</summary>
    public int CurrentGroupIndex => _groups.Count == 0 ? -1 : _currentGroup;

    /// <summary>A main-timeline desktop id to use as a fallback when tearing a group's desktops down.</summary>
    public DesktopId FallbackDesktopId =>
        _topRow.Count > 0 ? _topRow[Math.Clamp(_topIndex, 0, _topRow.Count - 1)].Id : _desktops.Current;

    public IEnumerable<DesktopId> GroupDesktopIds() => _groups.SelectMany(g => g.Desktops).Select(d => d.Id);

    private DesktopRef CurrentDesktop()
        => _onMain || _groups.Count == 0
            ? _topRow[Math.Clamp(_topIndex, 0, Math.Max(0, _topRow.Count - 1))]
            : _groups[_currentGroup].Desktops[_groups[_currentGroup].LastUsedIndex];

    /// <summary>Render-ready snapshot: main timeline + groups in their fixed stack order, split around
    /// main at <see cref="_currentGroup"/> (the groups before it render above main, the rest below).</summary>
    public NavMap BuildMap()
    {
        var top = new List<NavMapTile>(_topRow.Count);
        for (int i = 0; i < _topRow.Count; i++)
            top.Add(new NavMapTile(_topRow[i].Label, _onMain && i == _topIndex));

        var groups = new List<NavMapGroup>(_groups.Count);
        for (int gi = 0; gi < _groups.Count; gi++)
        {
            Group g = _groups[gi];
            bool current = !_onMain && gi == _currentGroup;
            var tiles = new List<NavMapTile>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
                tiles.Add(new NavMapTile(g.Desktops[j].Label, current && j == g.LastUsedIndex));
            groups.Add(new NavMapGroup(gi, g.Name, tiles, current, g.LastUsedIndex));
        }

        int topPosition = _groups.Count == 0 ? 0 : Math.Clamp(_currentGroup, 0, _groups.Count);
        return new NavMap(top, _topRow.Count == 0 ? 0 : _topIndex, _onMain, groups, topPosition);
    }

    // ── Navigation (vertical "main above current") ─────────────────────────────────

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
        if (_onMain || _groups.Count == 0)
        {
            if (_topRow.Count == 0) return false;
            int next = Math.Clamp(_topIndex + delta, 0, _topRow.Count - 1);
            if (next == _topIndex) return false;
            _topIndex = next;
        }
        else
        {
            Group g = _groups[_currentGroup];
            int next = Math.Clamp(g.LastUsedIndex + delta, 0, g.Desktops.Count - 1);
            if (next == g.LastUsedIndex) return false;
            g.LastUsedIndex = next;
        }
        return Commit();
    }

    // Down: on main → re-enter the current group; in a group → advance to the next group (no main re-cross).
    private bool Down()
    {
        if (_groups.Count == 0) return false;
        if (_onMain)
        {
            _onMain = false;                       // re-enter the group directly below main
        }
        else
        {
            if (_currentGroup + 1 >= _groups.Count) return false; // at the bottom — no wrap
            _currentGroup++;
        }
        return Commit();
    }

    // Up: in a group → surface to main; on main → step into the group above main (currentGroup−1).
    private bool Up()
    {
        if (!_onMain)
        {
            _onMain = true;                        // surface to the main timeline
        }
        else
        {
            if (_currentGroup <= 0 || _groups.Count == 0) return false; // no group above main — no-op
            _currentGroup--;
            _onMain = false;                       // enter it
        }
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific main-timeline desktop.</summary>
    public bool GoToTop(int index)
    {
        if (index < 0 || index >= _topRow.Count) return false;
        _onMain = true;
        _topIndex = index;
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific desktop within a specific group.</summary>
    public bool GoToGroupDesktop(int groupIndex, int desktopIndex)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count) return false;
        Group g = _groups[groupIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Desktops.Count) return false;
        _onMain = false;
        _currentGroup = groupIndex;
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

    // ── Group management ─────────────────────────────────────────────────────────

    public void SyncTopRow()
    {
        var grouped = _groups.SelectMany(g => g.Desktops).Select(d => d.Id.Value).ToHashSet();
        _topRow = _desktops.List()
            .Where(d => !grouped.Contains(d.Id.Value))
            .Select(d => new DesktopRef(d.Id, string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name))
            .ToList();

        _topIndex = _topRow.Count == 0 ? 0 : Math.Clamp(_topIndex, 0, _topRow.Count - 1);
        ClampCurrentGroup();
    }

    /// <summary>Add a group at the top of the stack and make it the pivot below main. Diving from main
    /// enters it. Keeps the OS position: if inside another group, shift the pivot to stay on it.</summary>
    public void AddGroup(Group group)
    {
        _groups.Insert(0, group);
        if (_onMain) _currentGroup = 0;   // the new group becomes the dive target directly below main
        else _currentGroup++;             // existing selection shifted down one — keep pointing at it
        ClampCurrentGroup();
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
        AdjustForRemoval(index);
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
            AdjustForRemoval(groupIndex);
        }
        else
        {
            g.RemoveDesktopAt(desktopIndex);
        }
        return id;
    }

    // Keep _onMain/_currentGroup coherent after a group at removedIndex disappears.
    private void AdjustForRemoval(int removedIndex)
    {
        if (!_onMain)
        {
            if (_currentGroup == removedIndex) _onMain = true;   // we were in it → surface to main
            else if (_currentGroup > removedIndex) _currentGroup--;
        }
        else if (_currentGroup > removedIndex)
        {
            _currentGroup--;                                     // keep the pivot on the same group
        }
        ClampCurrentGroup();
    }

    private void ClampCurrentGroup()
    {
        if (_groups.Count == 0) { _currentGroup = 0; _onMain = true; }
        else _currentGroup = Math.Clamp(_currentGroup, 0, _groups.Count - 1);
    }

    /// <summary>Re-anchor to whatever desktop the OS is now showing (after a create/destroy).</summary>
    public void Resync()
    {
        SyncTopRow();
        DesktopId cur = _desktops.Current;

        int ti = _topRow.FindIndex(d => d.Id == cur);
        if (ti >= 0) { _onMain = true; _topIndex = ti; }
        else
        {
            for (int gi = 0; gi < _groups.Count; gi++)
            {
                int di = -1;
                for (int j = 0; j < _groups[gi].Desktops.Count; j++) if (_groups[gi].Desktops[j].Id == cur) di = j;
                if (di >= 0) { _onMain = false; _currentGroup = gi; _groups[gi].LastUsedIndex = di; break; }
            }
        }
        ClampCurrentGroup();

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
        _currentGroup = _groups.Count == 0 ? 0 : Math.Clamp(state.ActiveGroup, 0, _groups.Count - 1);
    }

    private void Save()
    {
        _store?.Save(new PersistedState
        {
            ActiveGroup = _currentGroup,
            Groups = _groups.Select(g => new PersistedGroup
            {
                Name = g.Name,
                LastUsedIndex = g.LastUsedIndex,
                Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
            }).ToList(),
        });
    }
}
