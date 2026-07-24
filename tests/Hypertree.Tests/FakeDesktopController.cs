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

    public void SwitchTo(DesktopId id)
    {
        Switches.Add(id);
        Current = id;
    }

    /// <summary>Simulate an external desktop deletion (e.g. the user removing it from Task View), so
    /// reconciliation can be tested. Removes it from the list; if it was current, falls back.</summary>
    public void Remove(DesktopId id, DesktopId fallback)
    {
        _desktops.RemoveAll(d => d.Id == id);
        if (Current == id) Current = fallback;
    }

    // Not exercised by the navigation model — the fake only needs Current + SwitchTo + Remove.
    public DesktopId Create(string name) => throw new NotSupportedException();
    public void Rename(DesktopId id, string name) => throw new NotSupportedException();
    public string GetName(DesktopId id) => _desktops.First(d => d.Id == id).Name;
    public void MoveWindowToDesktop(nint hwnd, DesktopId id) => throw new NotSupportedException();
    public void PinWindow(nint hwnd) { }
    public void UnpinWindow(nint hwnd) { }
}
