using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Status;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Guards the fix for the state-directory split: a redirect (HYPERTREE_STATE_DIR, or the test override that
/// stands in for it) must move <b>every</b> persisted file, not just <c>status.json</c>. Before the shared
/// <see cref="StateDirectory"/> resolver, the file stores hardcoded <c>%APPDATA%\hypertree</c>, so a
/// redirected or portable install silently split its state across two directories.
/// </summary>
/// <remarks>
/// In the same serial collection as <see cref="StatusFileTests"/> because it drives the process-global
/// override (via <c>StatusFile.OverrideDirectory</c>, which now delegates to <see cref="StateDirectory"/>).
/// </remarks>
[Collection(StatusFileCollection.Name)]
public sealed class StateDirectoryTests : IDisposable
{
    private readonly string _dir;

    public StateDirectoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N"));
        StatusFile.OverrideDirectory(_dir); // delegates to StateDirectory.Override
    }

    public void Dispose()
    {
        StatusFile.OverrideDirectory(null);
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void All_file_stores_resolve_under_the_redirected_directory()
    {
        Assert.Equal(_dir, StateDirectory.Path);
        Assert.StartsWith(_dir, new FileStateStore().Path);
        Assert.StartsWith(_dir, new FileSnapshotStore().Path);
        Assert.StartsWith(_dir, new FileMonitorLayoutStore().Path);
        Assert.StartsWith(_dir, new FileSettingsStore().Path);
        Assert.StartsWith(_dir, new FileSpatialStore().Path);
        Assert.StartsWith(_dir, StatusFile.FilePath);
    }

    [Fact]
    public void State_round_trips_through_the_redirected_directory()
    {
        var store = new FileStateStore();
        store.Save(new PersistedState { MainSlot = 2, Branches = { new PersistedBranch { Name = "feat" } } });

        // A real file was written under the redirect (not %APPDATA%), and reads back intact.
        Assert.True(File.Exists(store.Path));
        Assert.StartsWith(_dir, store.Path);
        PersistedState loaded = new FileStateStore().Load();
        Assert.Equal(2, loaded.MainSlot);
        Assert.Equal("feat", Assert.Single(loaded.Branches).Name);
    }

    [Fact]
    public void WriteAtomic_leaves_no_temp_file_behind()
    {
        string path = StateDirectory.Combine("atomic-probe.json");
        StateDirectory.WriteAtomic(path, "{}");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp")); // temp was moved onto the target, not left as a sibling
    }
}
