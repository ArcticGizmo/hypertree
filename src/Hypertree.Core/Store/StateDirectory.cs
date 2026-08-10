namespace Hypertree.Store;

/// <summary>
/// The one place Hypertree resolves its state directory. Honours the <see cref="OverrideVariable"/>
/// environment override (so a scratch or portable instance keeps its state elsewhere) with a
/// <c>%APPDATA%\hypertree</c> fallback, and creates the directory on demand.
/// </summary>
/// <remarks>
/// Every persisted file resolves through here — state, settings, spatial, snapshots, monitor layouts, and
/// the status file — so a redirect moves all of them together. Previously only the status file honoured the
/// override while the other stores hardcoded <c>%APPDATA%</c>, which silently split a redirected or portable
/// install's state across two directories. Writes go through <see cref="WriteAtomic"/> (temp file + replace)
/// so a crash mid-write can't corrupt a file — a guarantee the status file already had and the others didn't.
/// </remarks>
public static class StateDirectory
{
    /// <summary>Environment variable that relocates the whole state directory. Set it and every Hypertree
    /// process (the tray and <c>htree</c>) reads and writes there instead of <c>%APPDATA%</c>.</summary>
    public const string OverrideVariable = "HYPERTREE_STATE_DIR";

    private static string? _override;

    /// <summary>Redirect the state directory within this process, or back to the default with null. Tests
    /// only; a separate process is redirected with <see cref="OverrideVariable"/> instead.</summary>
    internal static void Override(string? dir) => _override = dir;

    /// <summary>The resolved state directory, created on demand.</summary>
    public static string Path
    {
        get
        {
            string dir = _override
                         ?? NonEmpty(Environment.GetEnvironmentVariable(OverrideVariable))
                         ?? System.IO.Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Full path to <paramref name="fileName"/> within the state directory (created on demand).</summary>
    public static string Combine(string fileName) => System.IO.Path.Combine(Path, fileName);

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically: serialise to a sibling temp
    /// file, then replace the target in a single filesystem operation, so a reader — or a crash mid-write —
    /// never observes a half-written file.
    /// </summary>
    public static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, contents);
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
