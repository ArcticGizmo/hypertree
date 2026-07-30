using Hypertree.Desktops;

namespace Hypertree.Scopes;

/// <summary>
/// The breadcrumb trail: desktops visited, in order, with a cursor for undo/redo. A visit is recorded
/// only when a navigation <b>transaction</b> completes — the modifier release at the end of a keyboard
/// gesture, or a discrete jump — never the intermediate steps, so holding Ctrl+Alt and arrowing across
/// five desktops leaves one crumb, not five. Undo (<see cref="Undo"/>) walks the cursor back along the
/// trail and redo (<see cref="Redo"/>) forward; neither rewrites it. A real navigation from mid-trail
/// truncates the forward tail and grows from there, exactly like a browser's history. Holds only
/// <see cref="DesktopId"/>s (labels are resolved at render time), no UI, so it's unit-testable.
/// </summary>
public sealed class NavHistory
{
    private readonly int _capacity;
    private readonly List<DesktopId> _entries = new();
    private int _cursor = -1;

    public NavHistory(int capacity = 32) => _capacity = Math.Max(2, capacity);

    /// <summary>The trail, oldest first.</summary>
    public IReadOnlyList<DesktopId> Entries => _entries;

    /// <summary>Where the cursor sits in <see cref="Entries"/> (-1 when the trail is empty). Entries
    /// after it are the redo tail.</summary>
    public int Cursor => _cursor;

    /// <summary>The entry under the cursor — where the trail believes we are — or null when empty.</summary>
    public DesktopId? Current => _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : null;

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor >= 0 && _cursor < _entries.Count - 1;

    /// <summary>
    /// A transaction ended: we navigated from <paramref name="from"/> and settled on <paramref name="to"/>.
    /// Truncates any redo tail (a new move branches history), reconnects the trail through
    /// <paramref name="from"/> when its tip isn't already there (first record, or an external switch moved
    /// us off-trail since the last one), then appends the destination and moves the cursor onto it.
    /// </summary>
    public void Record(DesktopId from, DesktopId to)
    {
        if (from == to) return;
        if (_cursor < _entries.Count - 1) _entries.RemoveRange(_cursor + 1, _entries.Count - _cursor - 1);
        if (_entries.Count == 0 || _entries[^1] != from) _entries.Add(from);
        if (_entries[^1] != to) _entries.Add(to);
        if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
        _cursor = _entries.Count - 1;
    }

    /// <summary>Step the cursor one entry back and return it, or null at the start of the trail.</summary>
    public DesktopId? Undo() => CanUndo ? _entries[--_cursor] : null;

    /// <summary>Step the cursor one entry forward and return it, or null at the end of the trail.</summary>
    public DesktopId? Redo() => CanRedo ? _entries[++_cursor] : null;

    /// <summary>
    /// The "back and forth" hop — the alt-tab of desktops. Bounces between the trail's two <b>newest</b>
    /// entries only: standing on the newest, it targets the one before it; standing anywhere else
    /// (including off-trail), it targets the newest. Repeated presses therefore just flip between the
    /// same two desktops. The cursor follows the target, so a real navigation taken after a hop branches
    /// history from where the hop left you (see <see cref="Record"/>). Null when the trail doesn't have
    /// two entries yet. Never returns <paramref name="current"/> itself — the newest two entries are
    /// distinct by construction (consecutive duplicates are never recorded).
    /// </summary>
    public DesktopId? Toggle(DesktopId current)
    {
        if (_entries.Count < 2) return null;
        bool back = current == _entries[^1];
        _cursor = _entries.Count - (back ? 2 : 1);
        return _entries[_cursor];
    }

    /// <summary>
    /// Drop every entry whose desktop no longer exists (deleted from Task View, torn down with a branch)
    /// and collapse the duplicates that removal leaves adjacent. The cursor stays on its entry when that
    /// survives, else on the nearest surviving entry behind it.
    /// </summary>
    public void Prune(Func<DesktopId, bool> alive)
    {
        if (_entries.Count == 0) return;
        var kept = new List<DesktopId>(_entries.Count);
        int cursor = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!alive(_entries[i])) continue;
            if (kept.Count == 0 || kept[^1] != _entries[i]) kept.Add(_entries[i]);
            if (i <= _cursor) cursor = kept.Count - 1;
        }
        _entries.Clear();
        _entries.AddRange(kept);
        _cursor = kept.Count == 0 ? -1 : Math.Clamp(cursor, 0, kept.Count - 1);
    }
}
