using System.Text;

namespace Hypertree;

/// <summary>
/// The sink for exceptions that are <b>deliberately swallowed</b>. Hypertree's interop and persistence
/// layers are full of best-effort calls — a status write, a registry read, a COM reorder — that must never
/// fault the caller, so they catch broadly and carry on. That posture is correct, but silently dropping
/// every failure means a field bug (a locked <c>state.json</c>, a shell that won't enumerate, a wedged
/// registry) leaves no trace at all, and the interop/IPC layer is exactly where the interesting bugs live.
/// </summary>
/// <remarks>
/// <para>Route those catches through <see cref="Swallowed"/>: the caller's behaviour is unchanged — the
/// exception is still swallowed — but a timestamped record lands in <c>diagnostics.log</c> beside the rest
/// of the state (so a redirected/portable install keeps it together via <see cref="Store.StateDirectory"/>),
/// making the failure visible after the fact.</para>
///
/// <para>It must survive being called from inside a catch on any thread, so it is locked, and its own write
/// is wrapped: if even the sink can't write (disk full, path unwritable) there is nowhere left to report to,
/// and it must never throw back into the best-effort caller. The file is rolled to a single <c>.1</c>
/// generation past <see cref="MaxBytes"/> so a persistent failure can't grow it without bound.</para>
///
/// <para>Lives in the root <c>Hypertree</c> namespace (not a <c>Hypertree.Diagnostics</c> one) so it is
/// reachable from every layer without a using, and so no namespace shares its name — the same name-shadowing
/// trap that steered <c>Palette</c> away from <c>Theme</c>.</para>
/// </remarks>
public static class Diagnostics
{
    /// <summary>Roll the log once it passes this size, keeping one previous generation. Small: these are
    /// rare best-effort failures, not a trace log, and a bounded file is friendlier to read and to ship.</summary>
    private const long MaxBytes = 128 * 1024;

    private static readonly object Gate = new();

    /// <summary>Full path to the diagnostics log, beside the rest of Hypertree's state.</summary>
    public static string FilePath => Store.StateDirectory.Combine("diagnostics.log");

    /// <summary>
    /// Record an exception a best-effort catch is about to swallow. <paramref name="context"/> is a short,
    /// stable label for the call site (e.g. <c>"FileStateStore.Save"</c>) so the log stays greppable. Never
    /// throws — a failure to record is itself swallowed, because there is nowhere left to report it.
    /// </summary>
    public static void Swallowed(Exception ex, string context)
    {
        try
        {
            string entry = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}  [{context}]{Environment.NewLine}" +
                           $"{ex}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
            {
                string path = FilePath;
                Roll(path);
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Last line of defence: if the sink itself can't write, there is nowhere left to report it, and
            // it must never fault the best-effort caller that invoked it from inside a catch.
        }
    }

    // Keep one previous generation and start fresh once the current file grows past the cap. Best-effort:
    // if the roll fails we simply keep appending to the current file rather than lose the record.
    private static void Roll(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, path + ".1", overwrite: true);
        }
        catch { /* rolling is best-effort; on failure we keep appending to the current file */ }
    }
}
