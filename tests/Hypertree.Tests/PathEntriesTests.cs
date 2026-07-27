using Hypertree.Platform;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the PATH edit the installer performs. Worth testing closely for a reason the rest of the suite
/// isn't: a bad edit here follows the user into every terminal they open, survives reboots, and gives no
/// clue where it came from.
/// </summary>
public class PathEntriesTests
{
    private const string Dir = @"C:\Users\me\AppData\Local\Hypertree\current";

    // ── Add ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_appends_to_an_existing_path()
    {
        Assert.Equal(@"C:\bin;C:\tools;" + Dir, PathEntries.Add(@"C:\bin;C:\tools", Dir));
    }

    [Fact]
    public void Add_handles_an_empty_or_missing_path()
    {
        Assert.Equal(Dir, PathEntries.Add(null, Dir));
        Assert.Equal(Dir, PathEntries.Add("", Dir));
        Assert.Equal(Dir, PathEntries.Add("   ", Dir));
    }

    [Fact]
    public void Add_does_not_leave_a_double_separator()
    {
        Assert.Equal(@"C:\bin;" + Dir, PathEntries.Add(@"C:\bin;", Dir));
    }

    [Fact]
    public void Add_reports_no_change_when_already_present()
    {
        // Null means "don't write" — so a re-register on update doesn't rewrite PATH for no reason.
        Assert.Null(PathEntries.Add(@"C:\bin;" + Dir, Dir));
    }

    [Theory]
    [InlineData(@"C:\bin;C:\Users\me\AppData\Local\Hypertree\current\")]   // trailing separator
    [InlineData(@"C:\bin;c:\users\me\appdata\local\hypertree\CURRENT")]     // different case
    [InlineData("C:\\bin;\"C:\\Users\\me\\AppData\\Local\\Hypertree\\current\"")] // quoted
    [InlineData(@"C:\bin; C:\Users\me\AppData\Local\Hypertree\current ")]   // padded
    public void Add_recognises_an_entry_that_is_already_there_in_another_spelling(string existing)
    {
        // Windows writes PATH entries all these ways. Missing the match would append a duplicate on every
        // single update.
        Assert.Null(PathEntries.Add(existing, Dir));
    }

    [Fact]
    public void Add_preserves_unexpanded_variables_in_other_entries()
    {
        // The whole reason the installer reads the raw registry value: these must survive untouched.
        string before = @"%JAVA_HOME%\bin;%USERPROFILE%\.dotnet\tools";
        Assert.Equal(before + ";" + Dir, PathEntries.Add(before, Dir));
    }

    // ── Remove ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_takes_the_entry_out()
    {
        Assert.Equal(@"C:\bin;C:\tools", PathEntries.Remove(@"C:\bin;" + Dir + @";C:\tools", Dir));
    }

    [Fact]
    public void Remove_reports_no_change_when_the_entry_is_absent()
    {
        // Critical: uninstalling something never registered must not rewrite PATH at all. Re-joining from
        // our own split would quietly normalise separators and drop empty entries the user may rely on.
        Assert.Null(PathEntries.Remove(@"C:\bin;C:\tools", Dir));
        Assert.Null(PathEntries.Remove("", Dir));
        Assert.Null(PathEntries.Remove(null, Dir));
    }

    [Fact]
    public void Remove_leaves_every_other_entry_exactly_as_it_was()
    {
        string before = @"%JAVA_HOME%\bin;" + Dir + @";C:\Program Files\Git\cmd";
        Assert.Equal(@"%JAVA_HOME%\bin;C:\Program Files\Git\cmd", PathEntries.Remove(before, Dir));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Hypertree\current\")]
    [InlineData(@"c:\users\me\appdata\local\hypertree\CURRENT")]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Hypertree\\current\"")]
    public void Remove_matches_the_other_spellings_too(string entry)
    {
        // An uninstall that failed to match its own entry would leave a dead directory on PATH forever.
        Assert.Equal(@"C:\bin", PathEntries.Remove(@"C:\bin;" + entry, Dir));
    }

    [Fact]
    public void Remove_clears_a_duplicate_that_an_earlier_build_may_have_left()
    {
        Assert.Equal(@"C:\bin", PathEntries.Remove(@"C:\bin;" + Dir + ";" + Dir + @"\", Dir));
    }

    [Fact]
    public void Remove_can_empty_the_path_entirely()
    {
        Assert.Equal("", PathEntries.Remove(Dir, Dir));
    }

    // ── Round trip ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Install_then_uninstall_restores_the_original_value()
    {
        // The property that actually matters to a user: we leave PATH as we found it.
        string original = @"%JAVA_HOME%\bin;C:\Program Files\Git\cmd;C:\Windows\System32";

        string added = PathEntries.Add(original, Dir)!;
        Assert.True(PathEntries.Contains(added, Dir));

        string removed = PathEntries.Remove(added, Dir)!;
        Assert.Equal(original, removed);
        Assert.False(PathEntries.Contains(removed, Dir));
    }

    [Fact]
    public void Repeated_registration_is_idempotent()
    {
        // Every update calls Register again; the entry must not accumulate.
        string path = PathEntries.Add(@"C:\bin", Dir)!;
        Assert.Null(PathEntries.Add(path, Dir));
        Assert.Null(PathEntries.Add(path, Dir));
    }

    [Fact]
    public void An_empty_directory_is_never_written()
    {
        // A hook running from somewhere we can't resolve must not append a stray separator.
        Assert.Null(PathEntries.Add(@"C:\bin", ""));
        Assert.Null(PathEntries.Add(@"C:\bin", "   "));
        Assert.Null(PathEntries.Remove(@"C:\bin", ""));
    }
}
