using Hypertree.Desktops;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the ambient watcher's comparison logic: it must notice a switch made outside Hypertree, stay
/// silent about Hypertree's own moves, and never let a bad read from the shell stop it reporting.
/// </summary>
public class DesktopPollTests
{
    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static FakeDesktopController Controller(int current = 0)
        => new(new[] { D(0), D(1), D(2) }, current);

    [Fact]
    public void A_tick_with_nothing_moved_reports_nothing()
    {
        var poll = new DesktopPoll(Controller());
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        Assert.False(poll.Tick());
        Assert.Empty(seen);
    }

    [Fact]
    public void An_external_switch_is_reported()
    {
        var desktops = Controller();
        var poll = new DesktopPoll(desktops);
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        desktops.JumpExternally(D(2)); // Win+Ctrl+Arrow / Task View — nothing told the model

        Assert.True(poll.Tick());
        Assert.Equal(new[] { D(2) }, seen);
        Assert.Equal(D(2), poll.Seen);
    }

    [Fact]
    public void The_same_switch_is_only_reported_once()
    {
        var desktops = Controller();
        var poll = new DesktopPoll(desktops);
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        desktops.JumpExternally(D(1));
        poll.Tick();
        poll.Tick();
        poll.Tick();

        Assert.Single(seen);
    }

    [Fact]
    public void An_acknowledged_move_is_not_reported_as_external()
    {
        // The app acknowledges its own navigation, so the tray doesn't re-anchor to a move it just made
        // — which would be harmless but pointless work on every single keystroke.
        var desktops = Controller();
        var poll = new DesktopPoll(desktops);
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        desktops.SwitchTo(D(2)); // Hypertree's own move
        poll.Acknowledge(desktops.Current);

        Assert.False(poll.Tick());
        Assert.Empty(seen);
    }

    [Fact]
    public void A_failing_read_is_swallowed_and_the_poll_keeps_working()
    {
        // The shell can restart under us. A watcher that died on one bad read would leave the app
        // permanently stale, which is the exact failure it exists to prevent.
        var desktops = new ThrowingController(D(0));
        var poll = new DesktopPoll(desktops);
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        desktops.Throw = true;
        Assert.False(poll.Tick());

        desktops.Throw = false;
        desktops.Now = D(1);
        Assert.True(poll.Tick());
        Assert.Equal(new[] { D(1) }, seen);
    }

    [Fact]
    public void A_shell_unreadable_at_startup_does_not_make_the_first_good_read_look_like_a_move()
    {
        // Construction couldn't establish a baseline at all. The first successful reading should be
        // adopted silently, not announced as though the user had switched desktop.
        var desktops = new ThrowingController(D(0)) { Throw = true };
        var poll = new DesktopPoll(desktops);
        var seen = new List<DesktopId>();
        poll.Changed += seen.Add;

        Assert.Null(poll.Seen); // no baseline yet

        desktops.Throw = false;
        Assert.False(poll.Tick());
        Assert.Empty(seen);
        Assert.Equal(D(0), poll.Seen);

        desktops.Now = D(1); // now a genuine move
        Assert.True(poll.Tick());
        Assert.Equal(new[] { D(1) }, seen);
    }

    /// <summary>A controller whose <see cref="Current"/> can be made to throw, standing in for the shell
    /// going away mid-session.</summary>
    private sealed class ThrowingController : IDesktopController
    {
        public bool Throw;
        public DesktopId Now;

        public ThrowingController(DesktopId now) => Now = now;

        public DesktopId Current => Throw ? throw new InvalidOperationException("shell gone") : Now;

        public int Count => 1;
        public IReadOnlyList<DesktopInfo> List() => new[] { new DesktopInfo(Now, "d", 0) };
        public IReadOnlyDictionary<DesktopId, int> WindowCounts() => new Dictionary<DesktopId, int>();
        public IReadOnlyList<WindowInfo> WindowsOn(DesktopId id) => Array.Empty<WindowInfo>();
        public IReadOnlyList<WindowInfo> WindowsElsewhere() => Array.Empty<WindowInfo>();
        public void SwitchTo(DesktopId id) => Now = id;
        public DesktopId Create(string name) => throw new NotSupportedException();
        public void Rename(DesktopId id, string name) { }
        public void Reorder(DesktopId id, int index) { }
        public void Remove(DesktopId id, DesktopId fallback) { }
        public string GetName(DesktopId id) => "d";
        public void MoveWindowToDesktop(nint hwnd, DesktopId id) { }
        public void PinWindow(nint hwnd) { }
        public void UnpinWindow(nint hwnd) { }
    }
}
