using System.Text.Json;

namespace Hypertree.Recipes;

/// <summary>The saved recipe library — a named set of workspace recipes, the way templates and layouts are
/// each their own list. Generated ones (from a branch capture) and hand-authored ones live together.</summary>
public sealed class PersistedRecipes
{
    public List<Recipe> Recipes { get; set; } = new();
}

/// <summary>Load/save the recipe library. Behind an interface so tests use an in-memory fake.</summary>
public interface IRecipeStore
{
    PersistedRecipes Load();
    void Save(PersistedRecipes recipes);
}

/// <summary>
/// Stores recipes as JSON under <c>%APPDATA%\hypertree\recipes.json</c>, beside <c>state.json</c>. All
/// reads/writes are best-effort: a missing or corrupt file yields an empty library rather than throwing, so
/// a bad file never blocks startup (mirrors <see cref="Store.FileStateStore"/>).
/// </summary>
public sealed class FileRecipeStore : IRecipeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileRecipeStore()
        : this(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree"))
    {
    }

    /// <summary>Testing seam: keep <c>recipes.json</c> in an explicit directory instead of the roaming
    /// profile, so a round-trip can be exercised without touching a real install's state.</summary>
    internal FileRecipeStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "recipes.json");
    }

    public PersistedRecipes Load()
    {
        try
        {
            if (!File.Exists(Path)) return new PersistedRecipes();
            return JsonSerializer.Deserialize<PersistedRecipes>(File.ReadAllText(Path)) ?? new PersistedRecipes();
        }
        catch
        {
            return new PersistedRecipes();
        }
    }

    public void Save(PersistedRecipes recipes)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(recipes, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
