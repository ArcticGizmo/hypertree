using Hypertree.Desktops;
using Hypertree.Store;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state, vertical model (F2 — "stable pivot"). The <b>main timeline</b>
/// (<see cref="_topRow"/>) is every OS desktop not assigned to a group; it sits at a <b>fixed slot</b>
/// in the vertical stack (<see cref="_mainSlot"/> = how many groups render above it). <b>Groups</b> are
/// a fixed vertical list that never reorders. The full top-to-bottom sequence of rows is therefore:
///   groups[0..mainSlot-1]  /  MAIN  /  groups[mainSlot..]
/// and navigation is a plain ladder that walks a cursor through it — <b>main never moves</b>:
///   • <b>Up</b> / <b>Down</b>: move the cursor one row up / down, crossing main in place (no leap).
///   • <b>Left/Right</b>: within the current row (main desktops, or the current group's desktops).
/// Groups above main stay above; groups below stay below. A newly-added group appears directly below
/// main. Ends clamp (no wrap). Holds no Win32/UI, so the whole feel is unit-testable against a fake.
/// </summary>
public sealed class NavigationModel
{
    private readonly IDesktopController _desktops;
    private readonly IStateStore? _store;
    private readonly List<Group> _groups = new();

    private List<DesktopRef> _topRow = new();
    private int _mainSlot;          // groups[0.._mainSlot-1] render above main; the rest below. Fixed.
    private bool _onMain = true;    // true = cursor on the main timeline; false = inside _groups[_currentGroup]
    private int _currentGroup;      // the group the cursor is in (valid only when !_onMain, else the resume group)
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

    /// <summary>The group to act on for "current group" commands: the one the cursor is in, or (on the
    /// main timeline) the group directly below main. -1 when there are no groups.</summary>
    public int CurrentGroupIndex
        => _groups.Count == 0 ? -1
         : _onMain ? Math.Min(_mainSlot, _groups.Count - 1)
         : _currentGroup;

    /// <summary>A main-timeline desktop id to use as a fallback when tearing a group's desktops down.</summary>
    public DesktopId FallbackDesktopId =>
        _topRow.Count > 0 ? _topRow[Math.Clamp(_topIndex, 0, _topRow.Count - 1)].Id : _desktops.Current;

    public IEnumerable<DesktopId> GroupDesktopIds() => _groups.SelectMany(g => g.Desktops).Select(d => d.Id);

    /// <summary>Describe a desktop by its OS id for the persistent status label: its display label and
    /// the name of the group it belongs to (null when it's a main-timeline desktop). Resolved by id so
    /// it stays correct even after a switch made outside Hypertree; falls back to the live OS name for
    /// an id we don't track yet.</summary>
    public (string? group, string label) Describe(DesktopId id)
    {
        foreach (Group g in _groups)
            foreach (DesktopRef d in g.Desktops)
                if (d.Id == id) return (g.Name, d.Label);
        foreach (DesktopRef d in _topRow)
            if (d.Id == id) return (null, d.Label);
        string name = _desktops.GetName(id);
        return (null, string.IsNullOrEmpty(name) ? "Desktop" : name);
    }

    private DesktopRef CurrentDesktop()
        => _onMain || _groups.Count == 0
            ? _topRow[Math.Clamp(_topIndex, 0, Math.Max(0, _topRow.Count - 1))]
            : _groups[_currentGroup].Desktops[_groups[_currentGroup].LastUsedIndex];

    /// <summary>Render-ready snapshot: main timeline + groups in their fixed stack order, split around
    /// main at its fixed slot (groups before the slot render above main, the rest below).
    /// <paramref name="cameFrom"/>, when supplied, marks that desktop with the green "here" outline —
    /// used during navigation to show where the current move started from (mirrors the jump preview).</summary>
    public NavMap BuildMap(DesktopId? cameFrom = null)
    {
        IReadOnlyDictionary<DesktopId, int> counts = _desktops.WindowCounts();
        int Windows(DesktopId id) => counts.TryGetValue(id, out int n) ? n : 0;
        bool CameFrom(DesktopId id) => cameFrom == id;

        var top = new List<NavMapTile>(_topRow.Count);
        for (int i = 0; i < _topRow.Count; i++)
            top.Add(new NavMapTile(_topRow[i].Label, _onMain && i == _topIndex,
                                   IsHere: CameFrom(_topRow[i].Id), WindowCount: Windows(_topRow[i].Id)));

        var groups = new List<NavMapGroup>(_groups.Count);
        for (int gi = 0; gi < _groups.Count; gi++)
        {
            Group g = _groups[gi];
            bool current = !_onMain && gi == _currentGroup;
            var tiles = new List<NavMapTile>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
                tiles.Add(new NavMapTile(g.Desktops[j].Label, current && j == g.LastUsedIndex,
                                         IsHere: CameFrom(g.Desktops[j].Id), WindowCount: Windows(g.Desktops[j].Id)));
            groups.Add(new NavMapGroup(gi, g.Name, tiles, current, g.LastUsedIndex));
        }

        return new NavMap(top, _topRow.Count == 0 ? 0 : _topIndex, _onMain, groups, Math.Clamp(_mainSlot, 0, _groups.Count));
    }

    // ── Navigation (stable pivot ladder) ───────────────────────────────────────────

    public bool Apply(NavAction action) => action switch
    {
        NavAction.MoveLeft => Move(-1),
        NavAction.MoveRight => Move(+1),
        NavAction.Dive => SetRow(CurrentRow() + 1),   // Down = one row lower
        NavAction.Surface => SetRow(CurrentRow() - 1), // Up = one row higher
        _ => false,
    };

    // The cursor's index in the combined row sequence: groups[0..mainSlot-1] / main / groups[mainSlot..].
    // Rows run 0.._groups.Count (main occupies index _mainSlot).
    private int CurrentRow()
        => _onMain ? _mainSlot : (_currentGroup < _mainSlot ? _currentGroup : _currentGroup + 1);

    // Move the cursor to a row in the combined sequence (clamped), then map it back to main/group.
    private bool SetRow(int row)
    {
        row = Math.Clamp(row, 0, _groups.Count);
        if (row == _mainSlot) _onMain = true;
        else { _onMain = false; _currentGroup = row < _mainSlot ? row : row - 1; }
        return Commit();
    }

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

    /// <summary>Click-to-navigate: jump to a specific main-timeline desktop. Main keeps its slot.</summary>
    public bool GoToTop(int index)
    {
        if (index < 0 || index >= _topRow.Count) return false;
        _onMain = true;
        _topIndex = index;
        return Commit();
    }

    /// <summary>Click-to-navigate: jump to a specific desktop within a specific group. Main keeps its slot.</summary>
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
        ClampState();
    }

    /// <summary>Add a group directly below the main timeline (at the main slot), leaving main and the
    /// groups above it in place. Keeps the OS position: a cursor already below main shifts down with it.</summary>
    public void AddGroup(Group group)
    {
        int at = Math.Clamp(_mainSlot, 0, _groups.Count);
        _groups.Insert(at, group);
        if (!_onMain && _currentGroup >= at) _currentGroup++; // existing selection shifted down one
        ClampState();
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
        _target = CurrentDesktop().Id; // re-anchor: removing the group you were in lands you on main
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

    // Keep main's slot and the cursor coherent after the group at removedIndex disappears.
    private void AdjustForRemoval(int removedIndex)
    {
        if (removedIndex < _mainSlot) _mainSlot--;              // a group above main went — main rises with it
        if (!_onMain)
        {
            if (_currentGroup == removedIndex) _onMain = true;  // we were in it → land on main
            else if (_currentGroup > removedIndex) _currentGroup--;
        }
        ClampState();
    }

    private void ClampState()
    {
        _mainSlot = Math.Clamp(_mainSlot, 0, _groups.Count);
        if (_groups.Count == 0) { _currentGroup = 0; _onMain = true; }
        else _currentGroup = Math.Clamp(_currentGroup, 0, _groups.Count - 1);
    }

    /// <summary>
    /// Reconcile against the OS: drop any group desktops the OS no longer has (e.g. the user deleted a
    /// desktop from Task View), remove groups left empty, refresh the top row, and re-anchor. Call this
    /// before surfacing the map/palette so stale records are never shown or navigated to.
    /// </summary>
    public void Reconcile()
    {
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        for (int gi = _groups.Count - 1; gi >= 0; gi--)
        {
            Group g = _groups[gi];
            for (int j = g.Count - 1; j >= 0; j--)
                if (!live.Contains(g.Desktops[j].Id.Value)) g.RemoveDesktopAt(j);
            if (g.Count == 0) { _groups.RemoveAt(gi); AdjustForRemoval(gi); }
        }
        Resync(); // rebuilds the top row from the live list, re-anchors to the OS current, saves
    }

    /// <summary>Re-anchor to whatever desktop the OS is now showing (after a create/destroy). Main keeps
    /// its slot; only the cursor moves.</summary>
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
        ClampState();

        _target = CurrentDesktop().Id;
        Save();
        Changed?.Invoke();
    }

    // ── Snapshots (named layout capture / restore) ───────────────────────────────

    /// <summary>Capture the whole current layout — main timeline + groups, each desktop keyed by its OS
    /// GUID — as a named <see cref="Snapshot"/> the caller can persist and later restore.</summary>
    public Snapshot CaptureSnapshot(string name) => new()
    {
        Name = name,
        MainSlot = _mainSlot,
        MainDesktops = _topRow.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        Groups = _groups.Select(g => new PersistedGroup
        {
            Name = g.Name,
            LastUsedIndex = g.LastUsedIndex,
            Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        }).ToList(),
    };

    /// <summary>
    /// Replace the group structure wholesale (used by snapshot restore). The caller is responsible for
    /// having the OS desktops the <paramref name="groups"/> reference already present; the main timeline
    /// is then re-derived from the live desktops not in a group, and the cursor re-anchors to whatever
    /// desktop the OS is currently showing.
    /// </summary>
    public void RestoreStructure(int mainSlot, IReadOnlyList<Group> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        _mainSlot = Math.Clamp(mainSlot, 0, _groups.Count);
        Resync(); // rebuilds the top row from the live list, re-anchors, saves, notifies
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
        // Migrate: prefer the persisted MainSlot; fall back to the pre-pivot ActiveGroup split.
        int slot = state.MainSlot != 0 ? state.MainSlot : state.ActiveGroup;
        _mainSlot = _groups.Count == 0 ? 0 : Math.Clamp(slot, 0, _groups.Count);
        _currentGroup = _groups.Count == 0 ? 0 : Math.Clamp(state.ActiveGroup, 0, _groups.Count - 1);
    }

    private void Save()
    {
        _store?.Save(new PersistedState
        {
            ActiveGroup = _currentGroup,
            MainSlot = _mainSlot,
            Groups = _groups.Select(g => new PersistedGroup
            {
                Name = g.Name,
                LastUsedIndex = g.LastUsedIndex,
                Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
            }).ToList(),
        });
    }
}
