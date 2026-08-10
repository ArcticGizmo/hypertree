using System.Text.Json;
using Hypertree.Spatial;

namespace Hypertree.Store;

/// <summary>
/// A named capture of the whole desktop layout — the main timeline plus every branch, <b>and</b> the
/// spatial arrangement (room positions and group colours) — so a working arrangement can be re-created
/// later. Reuses <see cref="PersistedDesktop"/>/<see cref="PersistedBranch"/> so the structural shape
/// matches the live state store. Desktops keep their OS GUID: a restore re-attaches to the same desktop
/// when it still exists, and re-creates it by <c>Label</c> when it doesn't.
///
/// The spatial facts (<see cref="GroupColors"/>, <see cref="Positions"/>) are the same two sparse
/// side-tables <c>SpatialState</c> keeps, captured for the ids this snapshot names. They are what lets a
/// restore reproduce the 2-D map rather than collapsing back to the default row layout. Sparse and
/// optional: a snapshot written before spatial mode (or of a never-arranged map) simply has empty tables,
/// and restore falls back to the derived defaults exactly as before.
/// </summary>
public sealed class Snapshot
{
    public string Name { get; set; } = "";

    /// <summary>The main timeline's fixed slot in the vertical stack (how many branches sit above main).</summary>
    public int MainSlot { get; set; }

    /// <summary>The unbranched main-timeline desktops, in order.</summary>
    public List<PersistedDesktop> MainDesktops { get; set; } = new();

    public List<PersistedBranch> Branches { get; set; } = new();

    /// <summary>Explicit group colour by the captured <c>Branch.Id</c> (GUID string) → <c>#RRGGBB</c>.
    /// Sparse — a group with no stored colour falls back to its palette default on restore.</summary>
    public Dictionary<string, string> GroupColors { get; set; } = new();

    /// <summary>Explicit room position by captured <c>DesktopId</c> (GUID string) → grid cell. Sparse — a
    /// desktop with no stored position falls back to the derived row layout on restore.</summary>
    public Dictionary<string, GridPos> Positions { get; set; } = new();

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

    public FileSnapshotStore() => Path = StateDirectory.Combine("snapshots.json");

    public IReadOnlyList<Snapshot> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new List<Snapshot>();
            return JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(Path)) ?? new List<Snapshot>();
        }
        catch (Exception ex)
        {
            Diagnostics.Swallowed(ex, "FileSnapshotStore.Load");
            return new List<Snapshot>();
        }
    }

    public void Save(IReadOnlyList<Snapshot> snapshots)
    {
        try { StateDirectory.WriteAtomic(Path, JsonSerializer.Serialize(snapshots, Options)); }
        // best-effort; losing a write is better than crashing the tray
        catch (Exception ex) { Diagnostics.Swallowed(ex, "FileSnapshotStore.Save"); }
    }
}
