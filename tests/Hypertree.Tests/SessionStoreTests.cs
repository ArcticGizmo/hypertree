using System;
using System.IO;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// A captured session round-trips through <see cref="FileSessionStore"/>, and a missing file yields an
/// empty set rather than throwing — the same best-effort contract as the other file stores, so a bad or
/// absent <c>sessions.json</c> never blocks startup.
/// </summary>
public class SessionStoreTests
{
    private static FileSessionStore StoreInTempDir()
        => new(Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Session_round_trips()
    {
        var store = StoreInTempDir();
        var branchId = Guid.NewGuid();
        var desktopId = Guid.NewGuid();
        store.Save(new PersistedSessions
        {
            Branches =
            {
                new PersistedBranchSession
                {
                    BranchId = branchId,
                    BranchName = "feat-123",
                    Desktops =
                    {
                        new PersistedDesktopSession
                        {
                            DesktopId = desktopId,
                            Apps =
                            {
                                new PersistedApp { Path = @"C:\Prog\Code.exe", Name = "Code" },
                                new PersistedApp { Path = @"C:\Prog\wt.exe", Name = "WindowsTerminal" },
                            },
                        },
                    },
                },
            },
        });

        PersistedBranchSession branch = Assert.Single(store.Load().Branches);
        Assert.Equal(branchId, branch.BranchId);
        Assert.Equal("feat-123", branch.BranchName);
        PersistedDesktopSession desktop = Assert.Single(branch.Desktops);
        Assert.Equal(desktopId, desktop.DesktopId);
        Assert.Equal(new[] { @"C:\Prog\Code.exe", @"C:\Prog\wt.exe" }, desktop.Apps.Select(a => a.Path));
    }

    [Fact]
    public void Missing_file_yields_an_empty_set()
    {
        Assert.Empty(StoreInTempDir().Load().Branches);
    }
}
