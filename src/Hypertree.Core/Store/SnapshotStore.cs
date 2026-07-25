using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypertree.Store;

/// <summary>
/// A named capture of the whole desktop layout — the main timeline plus every branch — so a working
/// arrangement can be re-created later. Reuses <see cref="PersistedDesktop"/>/<see cref="PersistedBranch"/>
/// so the on-disk shape matches the live state store. Desktops keep their OS GUID: a restore re-attaches
/// to the same desktop when it still exists, and re-creates it by <c>Label</c> when it doesn't.
/// </summary>
public sealed class Snapshot
{
    public string Name { get; set; } = "";

    /// <summary>The main timeline's fixed slot in the vertical stack (how many branches sit above main).</summary>
    public int MainSlot { get; set; }

    /// <summary>The unbranched main-timeline desktops, in order.</summary>
    public List<PersistedDesktop> MainDesktops { get; set; } = new();

    // On-disk key predates the group→branch rename; pinned so existing snapshot files keep loading.
    [JsonPropertyName("Groups")]
    public List<PersistedBranch> Branches { get; set; } = new();

    /// <summary>Total desktops the snapshot defines (main + every branch desktop).</summary>
    public int DesktopCount => MainDesktops.Count + Branches.Sum(g => g.Desktops.Count);
}

/// <summary>Load/save the named snapshots. Behind an interface so tests use an in-memory fake.</summary>
public interface ISnapshotStore
{
    IReadOnlyList<Snapshot> Load();
    void Save(IReadOnlyList<Snapshot> snapshots);
}

/// <summary>
/// Stores snapshots as JSON under <c>%APPDATA%\hypertree\snapshots.json</c>. Best-effort like
/// <see cref="FileStateStore"/>: a missing or corrupt file yields an empty list rather than throwing.
/// </summary>
public sealed class FileSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileSnapshotStore()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "snapshots.json");
    }

    public IReadOnlyList<Snapshot> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new List<Snapshot>();
            return JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(Path)) ?? new List<Snapshot>();
        }
        catch
        {
            return new List<Snapshot>();
        }
    }

    public void Save(IReadOnlyList<Snapshot> snapshots)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(snapshots, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
