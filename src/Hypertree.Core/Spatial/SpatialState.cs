using System.Text.Json;

namespace Hypertree.Spatial;

/// <summary>
/// The spatial map's own persisted state, kept deliberately <b>apart</b> from navigation state
/// (<c>state.json</c>) and settings (<c>settings.json</c>) in its own <c>spatial.json</c>. This is what lets
/// spatial mode be additive: the row model never reads or writes it, so it cannot destabilise navigation.
///
/// Two sparse side-tables, keyed by the stable ids the rest of the app already uses (stored as their GUID
/// strings, so the JSON is human-readable and key-type-agnostic):
/// <list type="bullet">
///   <item><see cref="GroupColors"/> — an explicit colour per group (a <c>Branch.Id</c>). Absent ⇒ the
///   palette default for that group's index.</item>
///   <item><see cref="Positions"/> — a grid position per desktop (a <c>DesktopId</c>). Absent ⇒ the derived
///   default layout, so an unplaced desktop still lands somewhere sensible.</item>
/// </list>
/// Both being sparse is the point: a fresh install has an empty file and everything falls back to defaults,
/// and only the user's actual moves/recolours are ever written.
/// </summary>
public sealed class SpatialState
{
    /// <summary>Explicit group colour by <c>Branch.Id</c> (GUID string) → <c>#RRGGBB</c>. Sparse.</summary>
    public Dictionary<string, string> GroupColors { get; set; } = new();

    /// <summary>Explicit desktop position by <c>DesktopId</c> (GUID string) → grid cell. Sparse.</summary>
    public Dictionary<string, GridPos> Positions { get; set; } = new();

    public string? Color(Guid groupId)
        => GroupColors.TryGetValue(groupId.ToString(), out string? c) ? c : null;

    public GridPos? Position(Guid desktopId)
        => Positions.TryGetValue(desktopId.ToString(), out GridPos p) ? p : null;

    public void SetColor(Guid groupId, string hex) => GroupColors[groupId.ToString()] = hex;
    public void SetPosition(Guid desktopId, GridPos pos) => Positions[desktopId.ToString()] = pos;

    /// <summary>Forget a desktop's stored position — used when a room is deleted, so a recreated desktop
    /// with the same id doesn't inherit a stale slot.</summary>
    public void ClearPosition(Guid desktopId) => Positions.Remove(desktopId.ToString());
}

/// <summary>Load/save the spatial state. Behind an interface so tests use an in-memory fake and non-Windows
/// heads can opt out.</summary>
public interface ISpatialStore
{
    SpatialState Load();
    void Save(SpatialState state);
}

/// <summary>
/// Stores spatial state as JSON under <c>%APPDATA%\hypertree\spatial.json</c>. All reads/writes are
/// best-effort: a missing or corrupt file yields empty (all-defaults) state rather than throwing, so a bad
/// file never blocks startup — mirroring <c>FileStateStore</c> and <c>FileSettingsStore</c>.
/// </summary>
public sealed class FileSpatialStore : ISpatialStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileSpatialStore() : this(Hypertree.Store.StateDirectory.Path) { }

    /// <summary>Testing seam: keep <c>spatial.json</c> in an explicit directory instead of the roaming
    /// profile, so a round-trip can be exercised without touching a real install.</summary>
    internal FileSpatialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "spatial.json");
    }

    public SpatialState Load()
    {
        try
        {
            if (!File.Exists(Path)) return new SpatialState();
            return JsonSerializer.Deserialize<SpatialState>(File.ReadAllText(Path)) ?? new SpatialState();
        }
        catch
        {
            return new SpatialState();
        }
    }

    public void Save(SpatialState state)
    {
        try { Hypertree.Store.StateDirectory.WriteAtomic(Path, JsonSerializer.Serialize(state, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
