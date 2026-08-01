using Hypertree.Launch;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The OS-free half of building the launcher's app list: <see cref="AppCatalogFilter.FromShortcuts"/>
/// deduping, dropping noise, and sorting a raw set of discovered shortcuts. The Windows catalog only
/// enumerates files and hands them here, so this is where the list's shape is actually decided.
/// </summary>
public class AppCatalogFilterTests
{
    [Fact]
    public void Sorts_alphabetically_case_insensitively()
    {
        var result = AppCatalogFilter.FromShortcuts(new[]
        {
            ("Zoom", @"C:\z.lnk"),
            ("acrobat", @"C:\a.lnk"),
            ("Notepad", @"C:\n.lnk"),
        });

        Assert.Equal(new[] { "acrobat", "Notepad", "Zoom" }, result.Select(e => e.Name));
    }

    [Fact]
    public void Dedupes_by_name_keeping_the_first_seen()
    {
        // The all-users Start menu is scanned before the per-user one, so the machine-wide shortcut wins.
        var result = AppCatalogFilter.FromShortcuts(new[]
        {
            ("Git Bash", @"C:\ProgramData\...\Git Bash.lnk"),
            ("git bash", @"C:\Users\me\...\Git Bash.lnk"),
        });

        AppEntry entry = Assert.Single(result);
        Assert.Equal(@"C:\ProgramData\...\Git Bash.lnk", entry.LaunchPath);
    }

    [Theory]
    [InlineData("Uninstall Steam")]
    [InlineData("Foobar Uninstall")]
    [InlineData("uninstall")]
    public void Drops_uninstaller_shortcuts(string name)
    {
        var result = AppCatalogFilter.FromShortcuts(new[] { (name, @"C:\x.lnk") });
        Assert.Empty(result);
    }

    [Fact]
    public void Keeps_apps_whose_name_merely_contains_uninstall()
    {
        // "Uninstall" only screens a whole-name / leading / trailing match — a normal app isn't caught.
        var result = AppCatalogFilter.FromShortcuts(new[] { ("Uninstaller Pro", @"C:\u.lnk") });
        Assert.Equal("Uninstaller Pro", Assert.Single(result).Name);
    }

    [Fact]
    public void Drops_blank_names_and_paths_and_trims()
    {
        var result = AppCatalogFilter.FromShortcuts(new[]
        {
            ("   ", @"C:\blank.lnk"),      // blank name
            ("No Path", "  "),             // blank path
            ("  Trimmed  ", @"C:\t.lnk"),  // surrounding whitespace stripped
        });

        AppEntry entry = Assert.Single(result);
        Assert.Equal("Trimmed", entry.Name);
    }
}
