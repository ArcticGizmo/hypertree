using System;
using System.IO;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The spatial side-tables round-trip through <see cref="FileSpatialStore"/> (spatial.json), and a missing
/// or corrupt file yields empty all-defaults state rather than throwing — the same best-effort contract
/// <c>FileStateStore</c> / <c>FileSettingsStore</c> hold, so a bad file never blocks startup.
/// </summary>
public class SpatialStoreTests
{
    private static FileSpatialStore StoreInTempDir()
        => new(Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N")));

    private static Guid G(int n) => new($"{n:D8}-0000-0000-0000-000000000000");

    [Fact]
    public void Colours_and_positions_round_trip()
    {
        var store = StoreInTempDir();
        var state = new SpatialState();
        state.SetColor(G(1), "#F4795B");
        state.SetPosition(G(10), new GridPos(3, -2));
        state.SetPosition(G(11), new GridPos(0, 4));
        store.Save(state);

        SpatialState loaded = store.Load();
        Assert.Equal("#F4795B", loaded.Color(G(1)));
        Assert.Equal(new GridPos(3, -2), loaded.Position(G(10)));
        Assert.Equal(new GridPos(0, 4), loaded.Position(G(11)));
    }

    [Fact]
    public void Missing_file_yields_empty_state()
    {
        SpatialState loaded = StoreInTempDir().Load();
        Assert.Empty(loaded.GroupColors);
        Assert.Empty(loaded.Positions);
        Assert.Null(loaded.Color(G(1)));
        Assert.Null(loaded.Position(G(10)));
    }

    [Fact]
    public void Corrupt_file_yields_empty_state_rather_than_throwing()
    {
        var store = StoreInTempDir();
        File.WriteAllText(store.Path, "{ this is not valid json ");
        SpatialState loaded = store.Load();
        Assert.Empty(loaded.Positions);
    }

    [Fact]
    public void ClearPosition_forgets_a_deleted_rooms_slot()
    {
        var state = new SpatialState();
        state.SetPosition(G(10), new GridPos(1, 1));
        state.ClearPosition(G(10));
        Assert.Null(state.Position(G(10)));
    }
}
