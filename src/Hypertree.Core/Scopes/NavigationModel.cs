using Hypertree.Desktops;
using Hypertree.Store;

namespace Hypertree.Scopes;

/// <summary>
/// Model P as pure state, vertical model (F2 — "stable pivot"). The <b>main timeline</b>
/// (<see cref="_topRow"/>) is every OS desktop not assigned to a branch; it sits at a <b>fixed slot</b>
/// in the vertical stack (<see cref="_mainSlot"/> = how many branches render above it). <b>Branches</b> are
/// a fixed vertical list that never reorders. The full top-to-bottom sequence of rows is therefore:
///   branches[0..mainSlot-1]  /  MAIN  /  branches[mainSlot..]
/// and navigation is a plain ladder that walks a cursor through it — <b>main never moves</b>:
///   • <b>Up</b> / <b>Down</b>: move the cursor one row up / down, crossing main in place (no leap).
///   • <b>Left/Right</b>: within the current row (main desktops, or the current branch's desktops).
/// Branches above main stay above; branches below stay below. A newly-added branch appears directly below
/// main. Ends clamp (no wrap). Holds no Win32/UI, so the whole feel is unit-testable against a fake.
/// </summary>
public sealed class NavigationModel
{
    private readonly IDesktopController _desktops;
    private readonly IStateStore? _store;
    private readonly List<Branch> _branches = new();

    private List<DesktopRef> _topRow = new();
    private int _mainSlot;          // branches[0.._mainSlot-1] render above main; the rest below. Fixed.
    private bool _onMain = true;    // true = cursor on the main timeline; false = inside _branches[_currentBranch]
    private int _currentBranch;      // the branch the cursor is in (valid only when !_onMain, else the resume branch)
    private int _topIndex;          // cursor within the main timeline
    private DesktopId _target;      // desktop the model last switched to

    public event Action? Changed;

    public NavigationModel(IDesktopController desktops, IStateStore? store = null)
    {
        _desktops = desktops;
        _store = store;

        RestoreBranches();
        SyncTopRow();
        int idx = _topRow.FindIndex(d => d.Id == desktops.Current);
        if (idx >= 0) _topIndex = idx;
        _target = CurrentDesktop().Id;
    }

    // ── Queries ──────────────────────────────────────────────────────────────────

    public bool OnTop => _onMain;
    public int BranchCount => _branches.Count;
    public int CurrentTopIndex => _topIndex;

    /// <summary>The (branch, desktop) currently selected, or null when on the main timeline.</summary>
    public (int branch, int desktop)? CurrentBranchDesktop
        => _onMain || _branches.Count == 0 ? null : (_currentBranch, _branches[_currentBranch].LastUsedIndex);

    /// <summary>The branch to act on for "current branch" commands: the one the cursor is in, or (on the
    /// main timeline) the branch directly below main. -1 when there are no branches.</summary>
    public int CurrentBranchIndex
        => _branches.Count == 0 ? -1
         : _onMain ? Math.Min(_mainSlot, _branches.Count - 1)
         : _currentBranch;

    /// <summary>A main-timeline desktop id to use as a fallback when tearing a branch's desktops down.</summary>
    public DesktopId FallbackDesktopId =>
        _topRow.Count > 0 ? _topRow[Math.Clamp(_topIndex, 0, _topRow.Count - 1)].Id : _desktops.Current;

    public IEnumerable<DesktopId> BranchDesktopIds() => _branches.SelectMany(g => g.Desktops).Select(d => d.Id);

    /// <summary>Describe a desktop by its OS id for the persistent status label: its display label and
    /// the name of the branch it belongs to (null when it's a main-timeline desktop). Resolved by id so
    /// it stays correct even after a switch made outside Hypertree; falls back to the live OS name for
    /// an id we don't track yet.</summary>
    public (string? branch, string label) Describe(DesktopId id)
    {
        foreach (Branch g in _branches)
            foreach (DesktopRef d in g.Desktops)
                if (d.Id == id) return (g.Name, d.Label);
        foreach (DesktopRef d in _topRow)
            if (d.Id == id) return (null, d.Label);
        string name = _desktops.GetName(id);
        return (null, string.IsNullOrEmpty(name) ? "Desktop" : name);
    }

    private DesktopRef CurrentDesktop()
        => _onMain || _branches.Count == 0
            ? _topRow[Math.Clamp(_topIndex, 0, Math.Max(0, _topRow.Count - 1))]
            : _branches[_currentBranch].Desktops[_branches[_currentBranch].LastUsedIndex];

    /// <summary>Render-ready snapshot: main timeline + branches in their fixed stack order, split around
    /// main at its fixed slot (branches before the slot render above main, the rest below).
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

        var branches = new List<NavMapBranch>(_branches.Count);
        for (int gi = 0; gi < _branches.Count; gi++)
        {
            Branch g = _branches[gi];
            bool current = !_onMain && gi == _currentBranch;
            var tiles = new List<NavMapTile>(g.Desktops.Count);
            for (int j = 0; j < g.Desktops.Count; j++)
                tiles.Add(new NavMapTile(g.Desktops[j].Label, current && j == g.LastUsedIndex,
                                         IsHere: CameFrom(g.Desktops[j].Id), WindowCount: Windows(g.Desktops[j].Id)));
            branches.Add(new NavMapBranch(gi, g.Name, tiles, current, g.LastUsedIndex));
        }

        return new NavMap(top, _topRow.Count == 0 ? 0 : _topIndex, _onMain, branches, Math.Clamp(_mainSlot, 0, _branches.Count));
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

    // The cursor's index in the combined row sequence: branches[0..mainSlot-1] / main / branches[mainSlot..].
    // Rows run 0.._branches.Count (main occupies index _mainSlot).
    private int CurrentRow()
        => _onMain ? _mainSlot : (_currentBranch < _mainSlot ? _currentBranch : _currentBranch + 1);

    // Move the cursor to a row in the combined sequence (clamped), then map it back to main/branch.
    private bool SetRow(int row)
    {
        row = Math.Clamp(row, 0, _branches.Count);
        if (row == _mainSlot) _onMain = true;
        else { _onMain = false; _currentBranch = row < _mainSlot ? row : row - 1; }
        return Commit();
    }

    private bool Move(int delta)
    {
        if (_onMain || _branches.Count == 0)
        {
            if (_topRow.Count == 0) return false;
            int next = Math.Clamp(_topIndex + delta, 0, _topRow.Count - 1);
            if (next == _topIndex) return false;
            _topIndex = next;
        }
        else
        {
            Branch g = _branches[_currentBranch];
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

    /// <summary>Click-to-navigate: jump to a specific desktop within a specific branch. Main keeps its slot.</summary>
    public bool GoToBranchDesktop(int branchIndex, int desktopIndex)
    {
        if (branchIndex < 0 || branchIndex >= _branches.Count) return false;
        Branch g = _branches[branchIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Desktops.Count) return false;
        _onMain = false;
        _currentBranch = branchIndex;
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

    // ── Branch management ─────────────────────────────────────────────────────────

    public void SyncTopRow()
    {
        var inBranches = _branches.SelectMany(g => g.Desktops).Select(d => d.Id.Value).ToHashSet();
        _topRow = _desktops.List()
            .Where(d => !inBranches.Contains(d.Id.Value))
            .Select(d => new DesktopRef(d.Id, string.IsNullOrEmpty(d.Name) ? $"Desktop {d.Index + 1}" : d.Name))
            .ToList();

        _topIndex = _topRow.Count == 0 ? 0 : Math.Clamp(_topIndex, 0, _topRow.Count - 1);
        ClampState();
    }

    /// <summary>Add a branch directly below the main timeline (at the main slot), leaving main and the
    /// branches above it in place. Keeps the OS position: a cursor already below main shifts down with it.</summary>
    public void AddBranch(Branch branch)
    {
        int at = Math.Clamp(_mainSlot, 0, _branches.Count);
        _branches.Insert(at, branch);
        if (!_onMain && _currentBranch >= at) _currentBranch++; // existing selection shifted down one
        ClampState();
        SyncTopRow();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Add a branch directly below a <b>selection anchor</b> rather than below main. A branch is a
    /// row in the vertical stack, so "below the selection" means below the selected row: below main when the
    /// selection is on the main timeline (identical to <see cref="AddBranch"/>), or directly below the
    /// selected branch — which may sit above main (main then sinks one slot to keep it in place) or below it.</summary>
    public void AddBranchBelow(bool onMain, int branchIndex, Branch branch)
    {
        int at;
        if (onMain || _branches.Count == 0)
        {
            at = Math.Clamp(_mainSlot, 0, _branches.Count); // directly below main
        }
        else
        {
            branchIndex = Math.Clamp(branchIndex, 0, _branches.Count - 1);
            at = branchIndex + 1;                       // right after the selected branch
            if (branchIndex < _mainSlot) _mainSlot++;   // it's above main → keep the above-count, main sinks one
        }
        _branches.Insert(at, branch);
        if (!_onMain && _currentBranch >= at) _currentBranch++; // existing selection shifted down one
        ClampState();
        SyncTopRow();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Remove the branch at <paramref name="index"/> and return it (for desktop teardown).</summary>
    public Branch? RemoveBranch(int index)
    {
        if (index < 0 || index >= _branches.Count) return null;
        Branch removed = _branches[index];
        _branches.RemoveAt(index);
        AdjustForRemoval(index);
        SyncTopRow();
        _target = CurrentDesktop().Id; // re-anchor: removing the branch you were in lands you on main
        Save();
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Update the stored label for a main-timeline or branch desktop after an OS rename, so the
    /// map and the status label reflect it immediately (without waiting for a reconcile). Main-timeline
    /// labels also re-derive from the OS name on the next <see cref="SyncTopRow"/>; branch labels are the
    /// model's own, so this is the only path that changes them.</summary>
    public void SetDesktopLabel(bool onMain, int branchIndex, int desktopIndex, string label)
    {
        if (onMain)
        {
            if (desktopIndex < 0 || desktopIndex >= _topRow.Count) return;
            _topRow[desktopIndex] = _topRow[desktopIndex] with { Label = label };
        }
        else
        {
            if (branchIndex < 0 || branchIndex >= _branches.Count) return;
            _branches[branchIndex].SetLabel(desktopIndex, label);
        }
        Save();
        Changed?.Invoke();
    }

    // ── Single-desktop deletion (× badge / delete button) ─────────────────────────

    public int TotalDesktops => _topRow.Count + _branches.Sum(g => g.Count);

    public DesktopId? TopDesktopId(int index)
        => index >= 0 && index < _topRow.Count ? _topRow[index].Id : null;

    public (DesktopId id, string label)? PeekTopDesktop(int index)
        => index >= 0 && index < _topRow.Count ? (_topRow[index].Id, _topRow[index].Label) : null;

    public (DesktopId id, string label)? PeekBranchDesktop(int branchIndex, int desktopIndex)
        => branchIndex >= 0 && branchIndex < _branches.Count
           && desktopIndex >= 0 && desktopIndex < _branches[branchIndex].Count
            ? (_branches[branchIndex].Desktops[desktopIndex].Id, _branches[branchIndex].Desktops[desktopIndex].Label)
            : null;

    /// <summary>
    /// Detach a desktop from a branch so the caller can destroy it. Returns the id (null if invalid).
    /// If it was the branch's last desktop, the whole branch is removed. Pure mutation — the caller
    /// destroys the OS desktop then calls <see cref="Resync"/>.
    /// </summary>
    public DesktopId? DetachBranchDesktop(int branchIndex, int desktopIndex)
    {
        if (branchIndex < 0 || branchIndex >= _branches.Count) return null;
        Branch g = _branches[branchIndex];
        if (desktopIndex < 0 || desktopIndex >= g.Count) return null;

        DesktopId id = g.Desktops[desktopIndex].Id;
        if (g.Count == 1)
        {
            _branches.RemoveAt(branchIndex);
            AdjustForRemoval(branchIndex);
        }
        else
        {
            g.RemoveDesktopAt(desktopIndex);
        }
        return id;
    }

    // Keep main's slot and the cursor coherent after the branch at removedIndex disappears.
    private void AdjustForRemoval(int removedIndex)
    {
        if (removedIndex < _mainSlot) _mainSlot--;              // a branch above main went — main rises with it
        if (!_onMain)
        {
            if (_currentBranch == removedIndex) _onMain = true;  // we were in it → land on main
            else if (_currentBranch > removedIndex) _currentBranch--;
        }
        ClampState();
    }

    private void ClampState()
    {
        _mainSlot = Math.Clamp(_mainSlot, 0, _branches.Count);
        if (_branches.Count == 0) { _currentBranch = 0; _onMain = true; }
        else _currentBranch = Math.Clamp(_currentBranch, 0, _branches.Count - 1);
    }

    /// <summary>
    /// Reconcile against the OS: drop any branch desktops the OS no longer has (e.g. the user deleted a
    /// desktop from Task View), remove branches left empty, refresh the top row, and re-anchor. Call this
    /// before surfacing the map/palette so stale records are never shown or navigated to.
    /// </summary>
    public void Reconcile()
    {
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        for (int gi = _branches.Count - 1; gi >= 0; gi--)
        {
            Branch g = _branches[gi];
            for (int j = g.Count - 1; j >= 0; j--)
                if (!live.Contains(g.Desktops[j].Id.Value)) g.RemoveDesktopAt(j);
            if (g.Count == 0) { _branches.RemoveAt(gi); AdjustForRemoval(gi); }
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
            for (int gi = 0; gi < _branches.Count; gi++)
            {
                int di = -1;
                for (int j = 0; j < _branches[gi].Desktops.Count; j++) if (_branches[gi].Desktops[j].Id == cur) di = j;
                if (di >= 0) { _onMain = false; _currentBranch = gi; _branches[gi].LastUsedIndex = di; break; }
            }
        }
        ClampState();

        _target = CurrentDesktop().Id;
        Save();
        Changed?.Invoke();
    }

    // ── Snapshots (named layout capture / restore) ───────────────────────────────

    /// <summary>Capture the whole current layout — main timeline + branches, each desktop keyed by its OS
    /// GUID — as a named <see cref="Snapshot"/> the caller can persist and later restore.</summary>
    public Snapshot CaptureSnapshot(string name) => new()
    {
        Name = name,
        MainSlot = _mainSlot,
        MainDesktops = _topRow.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        Branches = _branches.Select(g => new PersistedBranch
        {
            Name = g.Name,
            LastUsedIndex = g.LastUsedIndex,
            Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
        }).ToList(),
    };

    /// <summary>
    /// Replace the branch structure wholesale (used by snapshot restore). The caller is responsible for
    /// having the OS desktops the <paramref name="branches"/> reference already present; the main timeline
    /// is then re-derived from the live desktops not in a branch, and the cursor re-anchors to whatever
    /// desktop the OS is currently showing.
    /// </summary>
    public void RestoreStructure(int mainSlot, IReadOnlyList<Branch> branches)
    {
        _branches.Clear();
        _branches.AddRange(branches);
        _mainSlot = Math.Clamp(mainSlot, 0, _branches.Count);
        Resync(); // rebuilds the top row from the live list, re-anchors, saves, notifies
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private void RestoreBranches()
    {
        if (_store is null) return;
        PersistedState state = _store.Load();
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        foreach (PersistedBranch pg in state.Branches)
        {
            var desks = pg.Desktops
                .Where(d => live.Contains(d.Id))
                .Select(d => new DesktopRef(new DesktopId(d.Id), d.Label))
                .ToList();
            if (desks.Count > 0) _branches.Add(new Branch(pg.Name, desks, pg.LastUsedIndex));
        }
        // Migrate: prefer the persisted MainSlot; fall back to the pre-pivot ActiveBranch split.
        int slot = state.MainSlot != 0 ? state.MainSlot : state.ActiveBranch;
        _mainSlot = _branches.Count == 0 ? 0 : Math.Clamp(slot, 0, _branches.Count);
        _currentBranch = _branches.Count == 0 ? 0 : Math.Clamp(state.ActiveBranch, 0, _branches.Count - 1);
    }

    private void Save()
    {
        _store?.Save(new PersistedState
        {
            ActiveBranch = _currentBranch,
            MainSlot = _mainSlot,
            Branches = _branches.Select(g => new PersistedBranch
            {
                Name = g.Name,
                LastUsedIndex = g.LastUsedIndex,
                Desktops = g.Desktops.Select(d => new PersistedDesktop { Id = d.Id.Value, Label = d.Label }).ToList(),
            }).ToList(),
        });
    }
}
