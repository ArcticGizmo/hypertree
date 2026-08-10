using Hypertree.Desktops;
using Hypertree.Spatial;
using Hypertree.Status;
using Hypertree.Store;

namespace Hypertree.Scopes;

/// <summary>Outcome of <see cref="NavigationModel.GoTo"/>. Distinguishes the two ways a target can fail
/// to resolve, so a caller can say which one went wrong.</summary>
public enum GoToResult
{
    Ok,
    /// <summary>No branch carries that id — it was removed, or dissolved when it lost its last desktop.</summary>
    NoSuchBranch,
    /// <summary>The row exists but has no desktop at that index.</summary>
    NoSuchDesktop,
}

/// <summary>
/// Model P as pure state, vertical model (F2 — "stable pivot"). The <b>main timeline</b>
/// (<see cref="_topRow"/>) is every OS desktop not assigned to a branch; it sits at a <b>fixed slot</b>
/// in the vertical stack (<see cref="_mainSlot"/> = how many branches render above it). <b>Branches</b> are
/// a vertical list that never reorders under the cursor — only an explicit re-slot from the map moves one
/// (<see cref="MoveBranchToRow"/>). The full top-to-bottom sequence of rows is therefore:
///   branches[0..mainSlot-1]  /  MAIN  /  branches[mainSlot..]
/// and navigation is a plain ladder that walks a cursor through it — <b>main never moves</b>:
///   • <b>Up</b> / <b>Down</b>: move the cursor one row up / down, crossing main in place (no leap).
///   • <b>Left/Right</b>: within the current row (main desktops, or the current branch's desktops).
/// Branches above main stay above; branches below stay below. A newly-added branch appears directly below
/// main. Ends clamp (no wrap). It commands the OS only through the injected <see cref="IDesktopController"/>
/// (navigation calls <c>SwitchTo</c>), and holds no Win32/UI itself — so the whole feel is unit-testable
/// against a fake controller. Render/status projection lives in <see cref="NavProjection"/>.
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
    private bool _backfilledIds;    // a restored branch predated ids, so the minted ones need writing back

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

        // Persist ids minted for branches that predate them, now rather than whenever the user next
        // happens to navigate. Otherwise an id we've already published — to the status file, and through
        // it to the CLI and Perch — would be re-minted on the next start, breaking the promise that it's
        // stable enough to store and come back to.
        if (_backfilledIds) Save();
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

    // An immutable read-only view of the current layout for the projection builders (see NavProjection).
    // Cheap to build and discarded per call — the projections run at summon time, not per keystroke.
    private NavLayout Layout() => new(
        _topRow, _branches, _onMain, _currentBranch, _topIndex,
        Math.Clamp(_mainSlot, 0, _branches.Count), RowOrder(), CurrentRow());

    /// <summary>Render-ready snapshot: main timeline + branches in their fixed stack order, split around
    /// main at its fixed slot (branches before the slot render above main, the rest below).
    /// <paramref name="cameFrom"/>, when supplied, marks that desktop with the green "here" outline —
    /// used during navigation to show where the current move started from (mirrors the jump preview).</summary>
    public NavMap BuildMap(DesktopId? cameFrom = null)
    {
        IReadOnlyDictionary<DesktopId, int> counts = _desktops.WindowCounts();
        return NavProjection.Map(Layout(), id => counts.TryGetValue(id, out int n) ? n : 0, cameFrom);
    }

    /// <summary>
    /// The id-carrying structural snapshot the <b>spatial</b> map is built from — the spatial twin of
    /// <see cref="BuildMap"/>. Same selection/here/window-count facts, but it keeps the <see cref="Branch.Id"/>
    /// and <see cref="DesktopId"/> that spatial state is keyed by (colour per group, position per desktop),
    /// which <see cref="NavMap"/> deliberately drops. Groups are emitted in the same draw order the rows use
    /// — branches above main, main (as the <see cref="Guid.Empty"/> "ungrouped" bucket), branches below — so
    /// the default spatial layout mirrors the row stack. Like <see cref="BuildMap"/>, it walks window counts,
    /// so it's a summon-time build, not a per-keystroke one.
    /// </summary>
    public SpatialSource BuildSpatialSource(DesktopId? cameFrom = null)
    {
        IReadOnlyDictionary<DesktopId, int> counts = _desktops.WindowCounts();
        return NavProjection.Spatial(Layout(), id => counts.TryGetValue(id, out int n) ? n : 0, cameFrom);
    }

    /// <summary>
    /// The stack as published to the outside world (the CLI, and anything else watching the status file):
    /// rows top-to-bottom with main in its slot, plus where the cursor actually is.
    /// </summary>
    /// <remarks>
    /// Deliberately not built on <see cref="BuildMap"/>. That call walks every top-level window through the
    /// documented desktop API to produce per-tile window counts — fine once, when a human summons the map,
    /// but this runs on every navigation, and nothing downstream of the status file wants the counts. So
    /// this reads only what it publishes.
    /// </remarks>
    public StatusSnapshot BuildStatus() => NavProjection.Status(Layout());

    // ── Navigation (stable pivot ladder) ───────────────────────────────────────────

    public bool Apply(NavAction action) => action switch
    {
        NavAction.MoveLeft => Move(-1),
        NavAction.MoveRight => Move(+1),
        NavAction.Dive => SetRow(CurrentRow() + 1),   // Down = one row lower
        NavAction.Surface => SetRow(CurrentRow() - 1), // Up = one row higher
        _ => false,
    };

    // ── Row order & cursor⇄row mapping ──────────────────────────────────────────────
    // The stack drawn top-to-bottom is branches[0..slot-1] / MAIN / branches[slot..]. Every projection (map,
    // spatial, status) and every re-slot walks this one order, and the cursor's row is derived from it here —
    // so the "splice main in at its slot" invariant and its off-by-one live in exactly one place.

    internal const int MainRowMarker = RowSplice.MainMarker; // shared with NavProjection; the one definition lives in RowSplice

    // Branch indices in draw order with main (MainRowMarker) spliced in at its clamped slot.
    private IReadOnlyList<int> RowOrder() => RowSplice.Order(_branches.Count, _mainSlot);

    // The cursor's index in that combined sequence. A branch below main is pushed down one row because main
    // occupies a row of its own.
    private int CurrentRow()
    {
        int slot = Math.Clamp(_mainSlot, 0, _branches.Count);
        return _onMain || _branches.Count == 0 ? slot : (_currentBranch < slot ? _currentBranch : _currentBranch + 1);
    }

    // The inverse: point the cursor at a combined-row index (caller clamps to 0.._branches.Count).
    private void CursorToRow(int row)
    {
        int slot = Math.Clamp(_mainSlot, 0, _branches.Count);
        if (row == slot) _onMain = true;
        else { _onMain = false; _currentBranch = row < slot ? row : row - 1; }
    }

    // Move the cursor to a row in the combined sequence (clamped), then map it back to main/branch.
    private bool SetRow(int row)
    {
        CursorToRow(Math.Clamp(row, 0, _branches.Count));
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

    /// <summary>The list index of the branch with this stable id, or -1 if no branch has it.</summary>
    public int IndexOfBranch(Guid id)
    {
        for (int i = 0; i < _branches.Count; i++) if (_branches[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// Jump to a row addressed the way the outside world addresses it: a branch by stable id, or main when
    /// <paramref name="branchId"/> is null, landing on <paramref name="desktop"/> or — when that is null —
    /// the row's remembered cursor, which is what a bare "go to this branch" means.
    /// </summary>
    /// <remarks>
    /// Resolution lives here rather than in the caller because an id has to be turned into a list index at
    /// the moment of the jump: a caller that read the layout and then acted on a position could land on a
    /// branch the user reordered in between. <paramref name="landed"/> reports where we ended up, for the
    /// caller to echo.
    /// </remarks>
    public GoToResult GoTo(Guid? branchId, int? desktop, out string landed)
    {
        landed = "";
        if (branchId is not { } id)
        {
            if (_topRow.Count == 0) return GoToResult.NoSuchDesktop;
            int mi = desktop ?? Math.Clamp(_topIndex, 0, _topRow.Count - 1);
            if (mi < 0 || mi >= _topRow.Count) return GoToResult.NoSuchDesktop;
            landed = $"main/{_topRow[mi].Label}";
            GoToTop(mi); // false only means "already there", which is a successful jump
            return GoToResult.Ok;
        }

        int bi = IndexOfBranch(id);
        if (bi < 0) return GoToResult.NoSuchBranch;
        Branch g = _branches[bi];
        int di = desktop ?? g.LastUsedIndex;
        if (di < 0 || di >= g.Count) return GoToResult.NoSuchDesktop;
        landed = $"{g.Name}/{g.Desktops[di].Label}";
        GoToBranchDesktop(bi, di);
        return GoToResult.Ok;
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

    /// <summary>
    /// Append a freshly created desktop to the branch at <paramref name="branchIndex"/> — "new desktop"
    /// while the map's selection is inside a branch lands in <em>that</em> branch rather than on main. The
    /// caller creates (and names) the OS desktop; this only records which branch claims it, and
    /// <see cref="SyncTopRow"/> then keeps it off the main timeline, since main is every OS desktop no
    /// branch has claimed. The branch's resume point stays where it was — creating doesn't switch.
    /// Returns its index within the branch, or null when there's no such branch (it may have dissolved
    /// while the prompt was open), in which case nothing was recorded and the desktop is still main's.
    /// </summary>
    public int? AddDesktopToBranch(int branchIndex, DesktopRef desktop)
    {
        if (branchIndex < 0 || branchIndex >= _branches.Count) return null;
        Branch g = _branches[branchIndex];
        g.InsertDesktop(g.Count, desktop);
        SyncTopRow();
        Save();
        Changed?.Invoke();
        return g.Count - 1;
    }

    // ── Reordering (map: Shift+arrows / drag) ─────────────────────────────────────

    /// <summary>
    /// Re-slot the branch at <paramref name="index"/> to <paramref name="row"/> in the combined row
    /// sequence — <c>branches[0..mainSlot-1] / MAIN / branches[mainSlot..]</c>, the order the map draws.
    /// Main is a row like any other here, so a branch stepping across it simply re-slots main (the
    /// "stable pivot" invariant is about navigation, not about the stack being unarrangeable). The branch
    /// keeps its desktops and the cursor stays with it, so nothing switches desktop. Returns the branch's
    /// new index, or null when the move is a no-op (out of range, or already on that row).
    /// </summary>
    public int? MoveBranchToRow(int index, int row)
    {
        if (index < 0 || index >= _branches.Count) return null;

        // The row sequence as a list of branch indices, with main (MainRowMarker) standing in for its slot.
        var seq = RowOrder().ToList();

        int at = seq.IndexOf(index);
        row = Math.Clamp(row, 0, seq.Count - 1);
        if (row == at) return null;
        seq.RemoveAt(at);
        seq.Insert(row, index);

        // Rebuild the stack in the new order, keeping the cursor (and the caller's branch) on the same
        // branch objects rather than on indices that have just shifted.
        Branch? cursorBranch = _onMain ? null : _branches[_currentBranch];
        Branch theBranch = _branches[index];
        var reordered = seq.Where(i => i != MainRowMarker).Select(i => _branches[i]).ToList();
        _mainSlot = seq.IndexOf(MainRowMarker);
        _branches.Clear();
        _branches.AddRange(reordered);
        if (cursorBranch is not null) _currentBranch = _branches.IndexOf(cursorBranch);
        ClampState();

        Save();
        Changed?.Invoke();
        return _branches.IndexOf(theBranch);
    }

    /// <summary>
    /// Re-slot the <b>main timeline</b> to <paramref name="row"/> in the combined sequence — the mirror of
    /// <see cref="MoveBranchToRow"/> for main itself (Shift+↑/↓ with main selected). Main's combined row is
    /// exactly <see cref="_mainSlot"/> (the branch count above it), so this just moves the slot; the branches
    /// keep their relative order and none switches desktop. This only re-slots main in the <em>stack</em> —
    /// the "stable pivot" invariant is about navigation not leaping over main, not about the stack being
    /// unarrangeable. Returns the row main landed on, or null on a no-op (out of range or unmoved).
    /// </summary>
    public int? MoveMainToRow(int row)
    {
        row = Math.Clamp(row, 0, _branches.Count);
        int slot = Math.Clamp(_mainSlot, 0, _branches.Count);
        if (row == slot) return null;

        _mainSlot = row;
        ClampState();
        Save();
        Changed?.Invoke();
        return row;
    }

    /// <summary>
    /// Move a single desktop to another slot in the stack: along its own row, into another branch, or
    /// on/off the main timeline. <paramref name="toIndex"/> is an <em>insertion point</em> in the
    /// destination row as the map draws it (0..count), so a drop between two tiles lands where the caret
    /// was. Nothing is created, destroyed or switched — but taking a branch's last desktop dissolves that
    /// branch (a branch can't be empty), and landing on main asks the OS to reorder its desktop list,
    /// since the main timeline <em>is</em> that order. Returns the desktop's new slot, or null if the move
    /// was rejected or resolved to a no-op.
    /// </summary>
    public (bool onMain, int branchIndex, int desktopIndex)? MoveDesktop(
        bool fromMain, int fromBranch, int fromDesktop, bool toMain, int toBranch, int toIndex)
    {
        DesktopRef? source = fromMain
            ? (fromDesktop >= 0 && fromDesktop < _topRow.Count ? _topRow[fromDesktop] : null)
            : (fromBranch >= 0 && fromBranch < _branches.Count
               && fromDesktop >= 0 && fromDesktop < _branches[fromBranch].Count
                ? _branches[fromBranch].Desktops[fromDesktop] : null);
        if (source is not { } moved) return null;
        if (!toMain && (toBranch < 0 || toBranch >= _branches.Count)) return null;
        // A record the OS has since lost (a desktop deleted from Task View) can't rejoin the main timeline —
        // the map is showing a ghost, and Reconcile will drop it. Refusing keeps "null means nothing
        // changed" true for the caller.
        if (toMain && _desktops.List().All(d => d.Id != moved.Id)) return null;

        if (fromMain)
        {
            // Main → main is purely an OS reorder; main → branch just claims the desktop (SyncTopRow then
            // drops it from the top row, which is "every desktop no branch has claimed").
            if (toMain && !ReorderOnMain(moved, toIndex)) return null;
            if (!toMain) _branches[toBranch].InsertDesktop(toIndex, moved);
        }
        else
        {
            Branch from = _branches[fromBranch];
            if (!toMain && toBranch == fromBranch)
            {
                if (!from.MoveDesktop(fromDesktop, toIndex)) return null;
            }
            else
            {
                from.RemoveDesktopAt(fromDesktop);
                if (from.Count == 0)
                {
                    _branches.RemoveAt(fromBranch);          // a branch can't be empty — it dissolves
                    AdjustForRemoval(fromBranch);
                    if (!toMain && toBranch > fromBranch) toBranch--; // the stack closed up behind it
                }
                // Leaving every branch already returns the desktop to main; the reorder is only about
                // honouring *where* on main it was dropped.
                if (toMain) ReorderOnMain(moved, toIndex);
                else _branches[toBranch].InsertDesktop(toIndex, moved);
            }
        }

        Resync(); // rebuild the top row, re-anchor on the desktop we're actually on, save, notify
        return Locate(moved.Id);
    }

    /// <summary>
    /// Reassign an existing desktop to the group identified by <paramref name="groupId"/> — another branch,
    /// or main (<see cref="Guid.Empty"/>, the ungrouped bucket) — appending it to that group's end. The
    /// spatial map's "set group" (g) for a destination that already exists: a thin wrapper over
    /// <see cref="MoveDesktop"/> that resolves the group id to a slot. Returns the desktop's new position,
    /// or null when the desktop or group can't be resolved, or it's already in that group (a no-op).
    /// </summary>
    public (bool onMain, int branchIndex, int desktopIndex)? MoveDesktopToGroup(DesktopId id, Guid groupId)
    {
        if (Locate(id) is not { } at) return null;
        if (groupId == Guid.Empty)
        {
            if (at.onMain) return null;                                   // already ungrouped
            return MoveDesktop(false, at.branchIndex, at.desktopIndex, true, -1, _topRow.Count);
        }
        int to = IndexOfBranch(groupId);
        if (to < 0) return null;
        if (!at.onMain && at.branchIndex == to) return null;             // already in that group
        return MoveDesktop(at.onMain, at.branchIndex, at.desktopIndex, false, to, _branches[to].Count);
    }

    /// <summary>
    /// Move an existing desktop out of wherever it lives (main, or another branch) and into a brand-new
    /// branch named <paramref name="name"/>, which it seeds as the sole desktop. The mirror of
    /// <see cref="MoveDesktopToGroup"/> for the "＋ create group" case, where the destination branch doesn't
    /// exist yet — a branch can't be created empty, so the moved desktop is what fills it. Taking a branch's
    /// last desktop dissolves that branch. Returns the new branch, or null if the desktop isn't tracked.
    /// </summary>
    public Branch? MoveDesktopToNewBranch(DesktopId id, string name)
    {
        if (Locate(id) is not { } at) return null;
        DesktopRef moved = at.onMain
            ? _topRow[at.desktopIndex]
            : _branches[at.branchIndex].Desktops[at.desktopIndex];

        if (!at.onMain)
        {
            Branch from = _branches[at.branchIndex];
            from.RemoveDesktopAt(at.desktopIndex);
            if (from.Count == 0) { _branches.RemoveAt(at.branchIndex); AdjustForRemoval(at.branchIndex); }
        }

        var branch = new Branch(name, new[] { moved });
        AddBranch(branch); // inserts at the main slot; SyncTopRow then drops the desktop off the main timeline
        return branch;
    }

    /// <summary>Where a desktop sits in the stack right now — main (branch index -1) or a branch — or null
    /// if we don't track it. Used to follow a desktop after a structural change moved it.</summary>
    public (bool onMain, int branchIndex, int desktopIndex)? Locate(DesktopId id)
    {
        for (int i = 0; i < _topRow.Count; i++)
            if (_topRow[i].Id == id) return (true, -1, i);
        for (int gi = 0; gi < _branches.Count; gi++)
            for (int j = 0; j < _branches[gi].Desktops.Count; j++)
                if (_branches[gi].Desktops[j].Id == id) return (false, gi, j);
        return null;
    }

    // Ask the OS to place `moved` at insertion point `insertAt` among the main timeline's desktops. Main's
    // order isn't ours to keep — it's read back from the OS every SyncTopRow — so honouring a drop position
    // means moving the desktop in the OS list, anchored to whichever main desktop it should sit next to.
    // Branch desktops interleaved in the OS order are ignored: only main's own neighbours are visible on
    // that row, so anchoring to them is what makes the board match. Best-effort — if the shell won't
    // reorder, the desktop still sits on main, just in its own slot.
    private bool ReorderOnMain(DesktopRef moved, int insertAt)
    {
        var others = new List<DesktopRef>(_topRow);
        int at = others.FindIndex(d => d.Id == moved.Id);
        if (at >= 0)
        {
            others.RemoveAt(at);
            if (insertAt > at) insertAt--; // lifting it out shifts everything after it left
        }
        if (others.Count == 0) return false; // the only desktop on main — nowhere to move it to
        insertAt = Math.Clamp(insertAt, 0, others.Count);

        IReadOnlyList<DesktopInfo> os = _desktops.List();
        int Ordinal(DesktopId id)
        {
            for (int i = 0; i < os.Count; i++) if (os[i].Id == id) return i;
            return -1;
        }

        int from = Ordinal(moved.Id);
        if (from < 0) return false;

        // Sit before others[insertAt], or after the last one when dropped past the end. The anchor's
        // ordinal shifts left by one if we're lifting the desktop out from in front of it.
        bool past = insertAt >= others.Count;
        int anchor = Ordinal(past ? others[^1].Id : others[insertAt].Id);
        if (anchor < 0) return false;
        int target = anchor - (from < anchor ? 1 : 0) + (past ? 1 : 0);
        if (target == from) return false;

        _desktops.Reorder(moved.Id, target);
        return true;
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

    /// <summary>The name of the branch at <paramref name="index"/>, or null when there's no such branch.</summary>
    public string? BranchNameAt(int index)
        => index >= 0 && index < _branches.Count ? _branches[index].Name : null;

    /// <summary>Rename the branch at <paramref name="index"/>, persisting and notifying. No-op off-range.</summary>
    public void RenameBranch(int index, string name)
    {
        if (index < 0 || index >= _branches.Count) return;
        _branches[index].SetName(name);
        Save();
        Changed?.Invoke();
    }

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

    /// <summary>
    /// Re-point the cursor at the desktop the OS is <em>actually</em> on, for when something outside
    /// Hypertree moved us there — another launcher jumping to a window, Task View, Win+Ctrl+Arrow. The
    /// layout is untouched (unlike <see cref="Resync"/>, which re-derives the top row), so this is cheap
    /// enough to run before every navigation keystroke; mid-gesture it's a no-op, since we're already
    /// standing on the desktop our own last switch put us on. Falls back to a full <see cref="Resync"/>
    /// when the OS is on a desktop we don't track yet (created behind our back). Returns true when the
    /// cursor actually moved.
    /// </summary>
    public bool AnchorToCurrent()
    {
        DesktopId cur = _desktops.Current;
        if (Locate(cur) is not { } at) { Resync(); return true; }

        bool alreadyThere = at.onMain
            ? _onMain && _topIndex == at.desktopIndex
            : !_onMain && _currentBranch == at.branchIndex
                       && _branches[at.branchIndex].LastUsedIndex == at.desktopIndex;
        _target = cur; // either way, this is the desktop the next Commit measures a move from
        if (alreadyThere) return false;

        if (at.onMain) { _onMain = true; _topIndex = at.desktopIndex; }
        else
        {
            _onMain = false;
            _currentBranch = at.branchIndex;
            _branches[at.branchIndex].LastUsedIndex = at.desktopIndex;
        }
        ClampState();
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Re-anchor to whatever desktop the OS is now showing (after a create/destroy). Main keeps
    /// its slot; only the cursor moves.</summary>
    public void Resync()
    {
        SyncTopRow();

        // Re-anchor onto whatever desktop the OS is showing, reusing the same scan the map/history use
        // (mirrors AnchorToCurrent). A desktop we don't track leaves the cursor put, then ClampState fixes up.
        if (Locate(_desktops.Current) is { } at)
        {
            if (at.onMain) { _onMain = true; _topIndex = at.desktopIndex; }
            else { _onMain = false; _currentBranch = at.branchIndex; _branches[at.branchIndex].LastUsedIndex = at.desktopIndex; }
        }
        ClampState();

        _target = CurrentDesktop().Id;
        Save();
        Changed?.Invoke();
    }

    // ── Snapshots (named layout capture / restore) ───────────────────────────────

    /// <summary>Capture the whole current layout — main timeline + branches, each desktop keyed by its OS
    /// GUID — as a named <see cref="Snapshot"/> the caller can persist and later restore. The branch
    /// <see cref="Branch.Id"/> is stamped on each captured branch so the caller can correlate the spatial
    /// group colour (keyed by that id) it layers on separately; structure restore still mints fresh ids, as
    /// a snapshot is a template.</summary>
    public Snapshot CaptureSnapshot(string name) => new()
    {
        Name = name,
        MainSlot = _mainSlot,
        MainDesktops = _topRow.Select(ToPersisted).ToList(),
        Branches = _branches.Select(ToPersisted).ToList(),
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

    // Project the in-memory model onto its persisted shapes. Shared by Save (the live state file) and
    // CaptureSnapshot (a named layout) so the two never drift — add a field to a branch/desktop and both
    // paths pick it up from here.
    private static PersistedDesktop ToPersisted(DesktopRef d) => new() { Id = d.Id.Value, Label = d.Label };
    private static PersistedBranch ToPersisted(Branch g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        LastUsedIndex = g.LastUsedIndex,
        Desktops = g.Desktops.Select(ToPersisted).ToList(),
    };

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
            // pg.Id is empty for state written before branch ids existed; Branch mints one, and the
            // constructor saves it back — so an upgrade backfills ids without a migration step.
            if (desks.Count == 0) continue;
            if (pg.Id == Guid.Empty) _backfilledIds = true;
            _branches.Add(new Branch(pg.Name, desks, pg.LastUsedIndex, pg.Id));
        }
        // Main defaults to first (slot 0) unless a slot was explicitly persisted, so it stays put at the top
        // instead of drifting to follow the active branch. A stored slot — including 0, and even the
        // pre-pivot layouts that had none — is honoured as the user's arrangement; only a genuinely absent
        // MainSlot (fresh install, or old state that never recorded one) falls back to first.
        int slot = state.MainSlot ?? 0;
        _mainSlot = _branches.Count == 0 ? 0 : Math.Clamp(slot, 0, _branches.Count);
        _currentBranch = _branches.Count == 0 ? 0 : Math.Clamp(state.ActiveBranch, 0, _branches.Count - 1);
    }

    private void Save()
    {
        _store?.Save(new PersistedState
        {
            ActiveBranch = _currentBranch,
            MainSlot = _mainSlot,
            Branches = _branches.Select(ToPersisted).ToList(),
        });
    }
}
