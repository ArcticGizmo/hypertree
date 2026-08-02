using System;
using System.IO;
using Hypertree.Loadouts;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The loadout library round-trips through <see cref="FileLoadoutStore"/>, including the nested
/// desktop/step/placement shape, and a missing file yields an empty library rather than throwing — the same
/// best-effort contract as the other file stores, so a bad or absent <c>loadouts.json</c> never blocks startup.
/// </summary>
public class LoadoutStoreTests
{
    private static FileLoadoutStore StoreInTempDir()
        => new(Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Loadout_round_trips_with_its_desktops_and_steps()
    {
        var store = StoreInTempDir();
        var loadout = new Loadout
        {
            Name = "feat-1",
            Desktops =
            {
                new LoadoutDesktop
                {
                    Label = "api",
                    Steps =
                    {
                        new LoadoutStep { Target = @"C:\Code.exe", Name = "Code", Hint = "myrepo — Code", Placement = new Placement { Desktop = "api", Monitor = 2 } },
                        new LoadoutStep { Target = @"C:\wt.exe", Name = "WindowsTerminal", Arguments = "-w 0", WorkingDirectory = @"C:\proj", Placement = new Placement { Desktop = "api" } },
                    },
                },
            },
        };
        store.Save(new PersistedLoadouts { Loadouts = { loadout } });

        Loadout loaded = Assert.Single(store.Load().Loadouts);
        Assert.Equal("feat-1", loaded.Name);
        LoadoutDesktop desktop = Assert.Single(loaded.Desktops);
        Assert.Equal("api", desktop.Label);
        Assert.Equal(new[] { @"C:\Code.exe", @"C:\wt.exe" }, desktop.Steps.Select(s => s.Target));
        Assert.Equal("api", desktop.Steps[0].Placement.Desktop);
        Assert.Equal(2, desktop.Steps[0].Placement.Monitor);
        Assert.Equal("myrepo — Code", desktop.Steps[0].Hint);
        Assert.Null(desktop.Steps[1].Placement.Monitor);
        Assert.Equal("-w 0", desktop.Steps[1].Arguments);
        Assert.Equal(@"C:\proj", desktop.Steps[1].WorkingDirectory);
    }

    [Fact]
    public void Missing_file_yields_an_empty_library()
    {
        Assert.Empty(StoreInTempDir().Load().Loadouts);
    }
}
