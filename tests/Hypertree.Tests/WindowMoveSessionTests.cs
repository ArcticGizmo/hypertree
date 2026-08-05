using Hypertree.Desktops;
using Hypertree.Scopes;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Exercises the phase-1 picker state of the "move windows" flow: focus clamping, multi-select
/// toggling, the ticked-set contents, and the Enter-with-nothing-selected convenience — all with no
/// Win32 or UI involved.
/// </summary>
public class WindowMoveSessionTests
{
    private static WindowInfo W(int hwnd, string title = "t", string proc = "p")
        => new(hwnd, title, proc);

    private static WindowMoveSession New(params int[] hwnds)
        => new(hwnds.Select(h => W(h)).ToList());

    [Fact]
    public void Empty_session_has_no_focused_window_and_moves_are_noops()
    {
        var s = new WindowMoveSession(new List<WindowInfo>());
        Assert.True(s.IsEmpty);
        Assert.Null(s.Focused);
        Assert.False(s.MoveFocus(1));
        s.ToggleSelected();                 // no-op, must not throw
        Assert.Equal(0, s.SelectedCount);
        Assert.False(s.EnsureFocusedSelected());
    }

    [Fact]
    public void Focus_starts_at_zero_and_clamps_at_both_ends()
    {
        var s = New(10, 11, 12);
        Assert.Equal(0, s.Focus);

        Assert.False(s.MoveFocus(-1));      // already at the start — no move
        Assert.Equal(0, s.Focus);

        Assert.True(s.MoveFocus(2));
        Assert.Equal(2, s.Focus);

        Assert.False(s.MoveFocus(5));       // clamps at the end — no move
        Assert.Equal(2, s.Focus);
    }

    [Fact]
    public void Toggle_selects_and_deselects_the_focused_window()
    {
        var s = New(10, 11, 12);
        s.ToggleSelected();                 // tick 10
        Assert.True(s.IsSelected(0));
        Assert.Equal(1, s.SelectedCount);

        s.ToggleSelected();                 // untick 10
        Assert.False(s.IsSelected(0));
        Assert.Equal(0, s.SelectedCount);
    }

    [Fact]
    public void SelectedHwnds_returns_ticked_windows_in_list_order()
    {
        var s = New(10, 11, 12);
        s.MoveFocus(2); s.ToggleSelected(); // tick 12
        s.MoveFocus(-2); s.ToggleSelected(); // tick 10 (focus back at 0)

        Assert.Equal(new nint[] { 10, 12 }, s.SelectedHwnds);
        Assert.Equal(2, s.SelectedCount);
    }

    [Fact]
    public void EnsureFocusedSelected_ticks_focused_only_when_nothing_selected()
    {
        var s = New(10, 11, 12);
        s.MoveFocus(1);                     // focus 11
        Assert.True(s.EnsureFocusedSelected());
        Assert.Equal(new nint[] { 11 }, s.SelectedHwnds);

        // Already has a selection → does not add the focused one on top.
        s.MoveFocus(1);                     // focus 12
        Assert.True(s.EnsureFocusedSelected());
        Assert.Equal(new nint[] { 11 }, s.SelectedHwnds);
    }

    [Fact]
    public void Filter_narrows_visible_by_title_or_process_case_insensitively()
    {
        var s = new WindowMoveSession(new[]
        {
            new WindowInfo(10, "Build log", "WindowsTerminal"),
            new WindowInfo(11, "Inbox", "chrome"),
            new WindowInfo(12, "Deploy", "WindowsTerminal"),
        });

        Assert.True(s.SetFilter("term"));           // matches process on 10 and 12
        Assert.Equal(new nint[] { 10, 12 }, s.Visible.Select(w => w.Hwnd));
        Assert.Equal(3, s.Windows.Count);           // full list is untouched

        Assert.True(s.SetFilter("inbox"));          // matches title on 11
        Assert.Equal(new nint[] { 11 }, s.Visible.Select(w => w.Hwnd));

        Assert.True(s.SetFilter(""));               // cleared → everything visible again
        Assert.Equal(3, s.Visible.Count);
        Assert.False(s.SetFilter(""));              // no change → returns false
    }

    [Fact]
    public void Filter_keeps_selection_by_handle_and_indexes_into_visible()
    {
        var s = new WindowMoveSession(new[]
        {
            new WindowInfo(10, "a", "alpha"),
            new WindowInfo(11, "b", "beta"),
            new WindowInfo(12, "c", "alpha"),
        });
        s.MoveFocus(1); s.ToggleSelected();         // tick 11

        s.SetFilter("alpha");                        // hides 11; visible = [10, 12]
        Assert.Equal(new nint[] { 10, 12 }, s.Visible.Select(w => w.Hwnd));
        s.ToggleSelected();                          // Focus snapped to 0 (10) → tick 10
        Assert.True(s.IsSelected(0));                // index 0 is now the visible 10

        // The out-of-view tick survives the filter — both come back when cleared.
        s.SetFilter("");
        Assert.Equal(new nint[] { 10, 11 }, s.SelectedHwnds);
    }

    [Fact]
    public void Filter_keeps_focus_on_the_same_window_when_it_survives()
    {
        var s = new WindowMoveSession(new[]
        {
            new WindowInfo(10, "a", "alpha"),
            new WindowInfo(11, "b", "beta"),
            new WindowInfo(12, "c", "beta"),
        });
        s.MoveFocus(2);                              // focus 12
        s.SetFilter("beta");                         // visible = [11, 12]; focus stays on 12
        Assert.Equal(1, s.Focus);
        Assert.Equal(12, s.Focused!.Hwnd);
    }
}
