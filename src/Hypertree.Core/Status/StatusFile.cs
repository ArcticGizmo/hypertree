using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Hypertree.Status;

/// <summary>
/// Reads and writes <c>%APPDATA%\hypertree\status.json</c> — the published <see cref="StatusSnapshot"/>.
/// Both halves live here so the tray and the CLI can never disagree about the shape or the location.
/// </summary>
/// <remarks>
/// Writes are atomic (temp file + replace) rather than in-place, so a reader can never observe a half-
/// written file. That's a deliberate choice to make the reader's job trivial: no locking protocol, no
/// torn-read tolerance, no retry logic beyond the microscopic window where the replace is in flight.
/// Everything here is best-effort in the house style — losing a status write must never take the tray
/// down, and an unreadable file must never take a reader down.
/// </remarks>
public static class StatusFile
{
    /// <summary>Bumped only for a breaking change to <see cref="StatusSnapshot"/>.</summary>
    public const int SchemaVersion = 1;

    // Source-generated rather than reflection-based, so htree can be published ahead-of-time compiled: a
    // CLI a human puts in their shell prompt runs on every command, and AOT is the difference between a
    // startup you notice and one you don't. Only the JsonTypeInfo overloads avoid the reflection that
    // trimming can't follow, so that is what's held here rather than a JsonSerializerOptions.
    private static JsonTypeInfo<StatusSnapshot> TypeInfo => StatusJsonContext.Default.StatusSnapshot;

    /// <summary>
    /// Environment variable that relocates the whole state directory. Set it and both the tray and
    /// <c>htree</c> read and write there instead of <c>%APPDATA%</c>.
    /// </summary>
    /// <remarks>
    /// Exists so a second Hypertree can be exercised against a scratch directory without disturbing the
    /// one the user actually runs — which is what makes an end-to-end test of the CLI possible at all,
    /// since the CLI is a separate process and can't be redirected from inside the test. It also covers
    /// the portable case, where someone wants state kept beside the executable rather than in a roaming
    /// profile.
    /// </remarks>
    public const string DirectoryVariable = "HYPERTREE_STATE_DIR";

    private static string? _directoryOverride;

    /// <summary>
    /// Redirect the status file within this process, or back to the default with null. Tests only; a
    /// separate process is redirected with <see cref="DirectoryVariable"/> instead.
    /// </summary>
    internal static void OverrideDirectory(string? dir) => _directoryOverride = dir;

    /// <summary>The directory Hypertree keeps its state in. Created on demand.</summary>
    public static string Directory
    {
        get
        {
            string dir = _directoryOverride
                         ?? NonEmpty(Environment.GetEnvironmentVariable(DirectoryVariable))
                         ?? Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static string FileName => "status.json";

    /// <summary>Full path to the status file. Doesn't imply it exists — absence means "no tray running".</summary>
    public static string FilePath => Path.Combine(Directory, FileName);

    /// <summary>
    /// Publish <paramref name="snapshot"/>. Serialises to a sibling temp file and replaces the real one in
    /// a single filesystem operation, so readers see either the whole previous version or the whole new one.
    /// </summary>
    public static void Write(StatusSnapshot snapshot)
    {
        try
        {
            string path = FilePath;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, TypeInfo));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort — a status write is never worth failing the tray over */ }
    }

    /// <summary>Remove the file on a clean exit, so nothing reports a tray that has gone.</summary>
    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { /* already gone / locked — nothing to do */ }
    }

    /// <summary>
    /// Read the published status, or null when there is none to read: no file (no tray has run, or it
    /// exited cleanly), a file left behind by a crashed tray (<see cref="StatusSnapshot.Pid"/> is dead),
    /// a schema this build doesn't know, or unparseable content.
    /// </summary>
    /// <remarks>
    /// The retry covers only the sub-millisecond window in which <see cref="Write"/>'s replace is in
    /// flight and the path momentarily can't be opened — not torn content, which atomic writes rule out.
    /// </remarks>
    public static StatusSnapshot? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return null;
                var snapshot = JsonSerializer.Deserialize(File.ReadAllText(path), TypeInfo);
                if (snapshot is null) return null;
                if (snapshot.Schema != SchemaVersion) return null;
                return IsAlive(snapshot.Pid) ? snapshot : null;
            }
            catch (IOException) { Thread.Sleep(15); } // mid-replace — try again
            catch { return null; }                    // malformed / unreadable — treat as no status
        }
        return null;
    }

    /// <summary>
    /// Whether the process that wrote the status file is still running. A clean exit deletes the file, but
    /// a kill or a crash can't — so a reader that trusted the file's mere existence would report a live
    /// tray, and a current-desktop marker, for something that isn't there.
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; } // no such process, or we can't see it — either way, don't trust the file
    }
}
