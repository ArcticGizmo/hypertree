using Hypertree.Desktops;

namespace Hypertree.Tests;

/// <summary>
/// In-memory <see cref="IDesktopController"/> for testing the navigation model with zero OS
/// involvement. Backed by a fixed list of desktops; records every SwitchTo so tests can assert
/// exactly which desktop the model drove to (and how many times).
/// </summary>
internal sealed class FakeDesktopController : IDesktopController
{
    private readonly List<DesktopInfo> _desktops;

    /// <summary>Every SwitchTo target, in order — the model's observable output.</summary>
    public List<DesktopId> Switches { get; } = new();

    public FakeDesktopController(IReadOnlyList<DesktopId> ids, int currentIndex = 0)
    {
        _desktops = ids.Select((id, i) => new DesktopInfo(id, $"d{i}", i)).ToList();
        Current = ids[currentIndex];
    }

    public int Count => _desktops.Count;
    public DesktopId Current { get; private set; }
    public IReadOnlyList<DesktopInfo> List() => _desktops;

    /// <summary>Per-desktop window counts a test can populate to exercise the map badges; empty by
    /// default, so every tile reads as zero windows.</summary>
    public Dictionary<DesktopId, int> WinCounts { get; } = new();
    public IReadOnlyDictionary<DesktopId, int> WindowCounts() => WinCounts;

    /// <summary>Per-desktop window lists a test can seed to exercise the move picker; empty by default.</summary>
    public Dictionary<DesktopId, List<WindowInfo>> Windows { get; } = new();
    public IReadOnlyList<WindowInfo> WindowsOn(DesktopId id)
        => Windows.TryGetValue(id, out var list) ? list : new List<WindowInfo>();

    /// <summary>Session-restore seam: a test seeds the global window list and each window's desktop; closes
    /// are recorded. Empty by default.</summary>
    public List<WindowInfo> AllWindowsList { get; } = new();
    public IReadOnlyList<WindowInfo> AllWindows() => AllWindowsList;

    public Dictionary<nint, DesktopId> WindowDesktop { get; } = new();
    public DesktopId? DesktopOf(nint hwnd) => WindowDesktop.TryGetValue(hwnd, out DesktopId d) ? d : null;

    /// <summary>Every CloseWindow call, in order — so restore's abort cleanup is assertable.</summary>
    public List<nint> Closed { get; } = new();
    public void CloseWindow(nint hwnd) => Closed.Add(hwnd);

    /// <summary>Every MoveWindowToMonitor call, in order — so restore's monitor placement is assertable.</summary>
    public List<(nint hwnd, int monitor)> MonitorMoves { get; } = new();
    public void MoveWindowToMonitor(nint hwnd, int monitor) => MonitorMoves.Add((hwnd, monitor));

    /// <summary>Every MoveWindowToDesktop call, in order — so the move flow's output is assertable.</summary>
    public List<(nint hwnd, DesktopId to)> Moves { get; } = new();

    public void SwitchTo(DesktopId id)
    {
        Switches.Add(id);
        Current = id;
    }

    /// <summary>Simulate a desktop switch made outside Hypertree (another launcher jumping to one of its
    /// windows, Task View, Win+Ctrl+Arrow): <see cref="Current"/> moves without the model ever asking, so
    /// nothing lands in <see cref="Switches"/> and the model's cursor is left behind.</summary>
    public void JumpExternally(DesktopId id) => Current = id;

    /// <summary>Simulate an external desktop deletion (e.g. the user removing it from Task View), so
    /// reconciliation can be tested. Removes it from the list; if it was current, falls back.</summary>
    public void Remove(DesktopId id, DesktopId fallback)
    {
        _desktops.RemoveAll(d => d.Id == id);
        if (Current == id) Current = fallback;
    }

    /// <summary>Every Reorder call, in order — so a main-timeline drop's OS reorder is assertable.</summary>
    public List<(DesktopId id, int index)> Reorders { get; } = new();

    /// <summary>Reorder like the shell does (remove, then insert at the given ordinal), so the fake's
    /// <see cref="List"/> — and therefore the model's re-derived main timeline — reflects the move.</summary>
    public void Reorder(DesktopId id, int index)
    {
        Reorders.Add((id, index));
        int at = _desktops.FindIndex(d => d.Id == id);
        if (at < 0) return;
        DesktopInfo moved = _desktops[at];
        _desktops.RemoveAt(at);
        _desktops.Insert(Math.Clamp(index, 0, _desktops.Count), moved);
        for (int i = 0; i < _desktops.Count; i++) _desktops[i] = _desktops[i] with { Index = i };
    }

    /// <summary>Append a desktop like the shell does. The model never creates (App does), but a test that
    /// exercises "new desktop" needs the created desktop to be live in <see cref="List"/> afterwards.</summary>
    public DesktopId Create(string name)
    {
        var id = new DesktopId(Guid.NewGuid());
        _desktops.Add(new DesktopInfo(id, name, _desktops.Count));
        return id;
    }

    // Not exercised by the navigation model — the fake only needs Current + SwitchTo + Remove.
    public void Rename(DesktopId id, string name) => throw new NotSupportedException();
    public string GetName(DesktopId id) => _desktops.First(d => d.Id == id).Name;
    public void MoveWindowToDesktop(nint hwnd, DesktopId id) => Moves.Add((hwnd, id));
    public void PinWindow(nint hwnd) { }
    public void UnpinWindow(nint hwnd) { }
}
