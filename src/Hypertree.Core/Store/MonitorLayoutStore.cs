using System.Text.Json;
using Hypertree.WindowLayout;

namespace Hypertree.Store;

/// <summary>The on-disk shape: the per-dock automatic captures, plus any user-named layouts.</summary>
public sealed class MonitorLayoutFile
{
    /// <summary>Automatic captures, one per monitor set (keyed by <see cref="MonitorLayoutSnapshot.SetKey"/>).</summary>
    public List<MonitorLayoutSnapshot> Auto { get; set; } = new();

    /// <summary>Layouts the user saved under a name (the manual save path).</summary>
    public List<NamedMonitorLayout> Named { get; set; } = new();
}

/// <summary>
/// Load/save monitor-layout captures. Behind an interface so <see cref="MonitorLayoutService"/> — and its
/// tests — use an in-memory fake. The typed helpers all resolve to a whole-file load/mutate/save, matching
/// <see cref="FileSnapshotStore"/>'s best-effort posture.
/// </summary>
public interface IMonitorLayoutStore
{
    /// <summary>The automatic capture for a monitor set, or null if none saved for it yet.</summary>
    MonitorLayoutSnapshot? GetAuto(string setKey);

    /// <summary>Upsert the automatic capture for its monitor set (replaces any prior capture of that set).</summary>
    void PutAuto(MonitorLayoutSnapshot snapshot);

    /// <summary>Every named layout, in save order.</summary>
    IReadOnlyList<NamedMonitorLayout> Named();

    /// <summary>Upsert a named layout (same name, case-insensitive, overwrites in place).</summary>
    void SaveNamed(NamedMonitorLayout layout);

    /// <summary>Remove a named layout; a missing name is a no-op.</summary>
    void DeleteNamed(string name);
}

/// <summary>
/// Stores monitor layouts as JSON under <c>%APPDATA%\hypertree\monitor-layouts.json</c>. Best-effort like
/// <see cref="FileStateStore"/> / <see cref="FileSnapshotStore"/>: a missing or corrupt file yields an
/// empty set rather than throwing, so a bad file never blocks the tray.
/// </summary>
public sealed class FileMonitorLayoutStore : IMonitorLayoutStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileMonitorLayoutStore()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "monitor-layouts.json");
    }

    private MonitorLayoutFile Load()
    {
        try
        {
            if (!File.Exists(Path)) return new MonitorLayoutFile();
            return JsonSerializer.Deserialize<MonitorLayoutFile>(File.ReadAllText(Path)) ?? new MonitorLayoutFile();
        }
        catch
        {
            return new MonitorLayoutFile();
        }
    }

    private void Save(MonitorLayoutFile file)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(file, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }

    public MonitorLayoutSnapshot? GetAuto(string setKey)
        => Load().Auto.FirstOrDefault(s => s.SetKey == setKey);

    public void PutAuto(MonitorLayoutSnapshot snapshot)
    {
        MonitorLayoutFile file = Load();
        file.Auto.RemoveAll(s => s.SetKey == snapshot.SetKey);
        file.Auto.Add(snapshot);
        Save(file);
    }

    public IReadOnlyList<NamedMonitorLayout> Named() => Load().Named;

    public void SaveNamed(NamedMonitorLayout layout)
    {
        MonitorLayoutFile file = Load();
        file.Named.RemoveAll(n => n.Name.Equals(layout.Name, StringComparison.OrdinalIgnoreCase));
        file.Named.Add(layout);
        Save(file);
    }

    public void DeleteNamed(string name)
    {
        MonitorLayoutFile file = Load();
        int removed = file.Named.RemoveAll(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save(file);
    }
}
