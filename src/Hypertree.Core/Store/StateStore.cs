using System.Text.Json;

namespace Hypertree.Store;

/// <summary>Persisted branch state — the anchor↔branch map that must survive a reboot (PLAN.md §5) so
/// Hypertree re-associates its created desktops instead of treating them as orphaned/unbranched.</summary>
public sealed class PersistedState
{
    /// <summary>The cursor's branch (resume point) when inside a branch.</summary>
    public int ActiveBranch { get; set; }

    /// <summary>The main timeline's fixed slot in the vertical stack: how many branches render above
    /// main. <c>Branches[0..MainSlot-1]</c> sit above main, the rest below (F2 stable pivot).</summary>
    public int MainSlot { get; set; }

    public List<PersistedBranch> Branches { get; set; } = new();
}

public sealed class PersistedBranch
{
    public string Name { get; set; } = "";
    public int LastUsedIndex { get; set; }
    public List<PersistedDesktop> Desktops { get; set; } = new();
}

public sealed class PersistedDesktop
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>Load/save the persisted state. Kept behind an interface so tests use an in-memory fake.</summary>
public interface IStateStore
{
    PersistedState Load();
    void Save(PersistedState state);
}

/// <summary>
/// Stores state as JSON under <c>%APPDATA%\hypertree\state.json</c>. All reads/writes are best-effort:
/// a missing or corrupt file yields empty state rather than throwing, so a bad file never blocks startup.
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileStateStore()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "state.json");
    }

    public PersistedState Load()
    {
        try
        {
            if (!File.Exists(Path)) return new PersistedState();
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(Path)) ?? new PersistedState();
        }
        catch
        {
            return new PersistedState();
        }
    }

    public void Save(PersistedState state)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(state, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
