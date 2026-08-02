using System.Text.Json;

namespace Hypertree.Loadouts;

/// <summary>The saved loadout library — a named set of workspace loadouts, the way templates and layouts are
/// each their own list. Generated ones (from a branch capture) and hand-authored ones live together.</summary>
public sealed class PersistedLoadouts
{
    public List<Loadout> Loadouts { get; set; } = new();
}

/// <summary>Load/save the loadout library. Behind an interface so tests use an in-memory fake.</summary>
public interface ILoadoutStore
{
    PersistedLoadouts Load();
    void Save(PersistedLoadouts loadouts);
}

/// <summary>
/// Stores loadouts as JSON under <c>%APPDATA%\hypertree\loadouts.json</c>, beside <c>state.json</c>. All
/// reads/writes are best-effort: a missing or corrupt file yields an empty library rather than throwing, so
/// a bad file never blocks startup (mirrors <see cref="Store.FileStateStore"/>).
/// </summary>
public sealed class FileLoadoutStore : ILoadoutStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileLoadoutStore()
        : this(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree"))
    {
    }

    /// <summary>Testing seam: keep <c>loadouts.json</c> in an explicit directory instead of the roaming
    /// profile, so a round-trip can be exercised without touching a real install's state.</summary>
    internal FileLoadoutStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "loadouts.json");
    }

    // The pre-rename file used a top-level "Recipes" property; a loadout is structurally identical to the old
    // recipe, so we read those objects straight into Loadout via this compat wrapper.
    private sealed class LegacyRecipes { public List<Loadout> Recipes { get; set; } = new(); }

    public PersistedLoadouts Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<PersistedLoadouts>(File.ReadAllText(Path)) ?? new PersistedLoadouts();

            // One-time migration: adopt a pre-rename recipes.json if there's no loadouts.json yet. The next
            // Save writes loadouts.json, after which the old file is ignored (and can be deleted by hand).
            string legacy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "recipes.json");
            if (File.Exists(legacy)
                && JsonSerializer.Deserialize<LegacyRecipes>(File.ReadAllText(legacy)) is { } old)
                return new PersistedLoadouts { Loadouts = old.Recipes };

            return new PersistedLoadouts();
        }
        catch
        {
            return new PersistedLoadouts();
        }
    }

    public void Save(PersistedLoadouts loadouts)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(loadouts, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
