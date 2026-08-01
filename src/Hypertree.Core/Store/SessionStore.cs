using System.Text.Json;

namespace Hypertree.Store;

/// <summary>One app in a persisted session: the full executable path to relaunch and a display name.
/// The stored form of <see cref="Launch.CapturedApp"/>.</summary>
public sealed class PersistedApp
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>The apps that were open on one desktop, keyed by the desktop's OS GUID (the same id the
/// navigation model persists), so restore can match them back to a live desktop.</summary>
public sealed class PersistedDesktopSession
{
    public Guid DesktopId { get; set; }
    public List<PersistedApp> Apps { get; set; } = new();
}

/// <summary>A whole branch's captured session — one entry per desktop that had capturable windows. Keyed
/// by <see cref="Scopes.Branch.Id"/>; <see cref="BranchName"/> is carried for readability only.</summary>
public sealed class PersistedBranchSession
{
    public Guid BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public List<PersistedDesktopSession> Desktops { get; set; } = new();
}

/// <summary>
/// The remembered "what was open" side-table (see docs/design/session-restore.md). Kept separate from
/// <see cref="PersistedState"/> because it is orthogonal to the navigation structure: state.json is
/// rebuilt from the live desktops on every change, whereas a session is a heavier payload captured on
/// demand and keyed by GUID, so it rides in its own file and neither one disturbs the other.
/// </summary>
public sealed class PersistedSessions
{
    public List<PersistedBranchSession> Branches { get; set; } = new();
}

/// <summary>Load/save captured sessions. Behind an interface so tests use an in-memory fake.</summary>
public interface ISessionStore
{
    PersistedSessions Load();
    void Save(PersistedSessions sessions);
}

/// <summary>
/// Stores sessions as JSON under <c>%APPDATA%\hypertree\sessions.json</c>, beside <c>state.json</c>. All
/// reads/writes are best-effort: a missing or corrupt file yields an empty set rather than throwing, so a
/// bad file never blocks startup (mirrors <see cref="FileStateStore"/>).
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; }

    public FileSessionStore()
        : this(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree"))
    {
    }

    /// <summary>Testing seam: keep <c>sessions.json</c> in an explicit directory instead of the roaming
    /// profile, so a round-trip can be exercised without touching a real install's state.</summary>
    internal FileSessionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "sessions.json");
    }

    public PersistedSessions Load()
    {
        try
        {
            if (!File.Exists(Path)) return new PersistedSessions();
            return JsonSerializer.Deserialize<PersistedSessions>(File.ReadAllText(Path)) ?? new PersistedSessions();
        }
        catch
        {
            return new PersistedSessions();
        }
    }

    public void Save(PersistedSessions sessions)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(sessions, Options)); }
        catch { /* best-effort; losing a write is better than crashing the tray */ }
    }
}
