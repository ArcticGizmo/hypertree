using Hypertree.Status;
using Hypertree.Store;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Guards the swallowed-exception sink: a best-effort catch that routes through
/// <see cref="Diagnostics.Swallowed"/> must leave a greppable record beside the rest of the state, must
/// never throw back into the caller, and must stay bounded so a persistent failure can't grow it forever.
/// </summary>
/// <remarks>
/// In the same serial collection as <see cref="StatusFileTests"/> because it drives the process-global state
/// directory redirect (via <c>StatusFile.OverrideDirectory</c> → <see cref="StateDirectory"/>).
/// </remarks>
[Collection(StatusFileCollection.Name)]
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _dir;

    public DiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N"));
        StatusFile.OverrideDirectory(_dir); // delegates to StateDirectory.Override
    }

    public void Dispose()
    {
        StatusFile.OverrideDirectory(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Swallowed_writes_the_context_and_exception_under_the_state_directory()
    {
        Diagnostics.Swallowed(new InvalidOperationException("boom"), "SomeStore.Save");

        Assert.StartsWith(_dir, Diagnostics.FilePath);
        Assert.True(File.Exists(Diagnostics.FilePath));
        string log = File.ReadAllText(Diagnostics.FilePath);
        Assert.Contains("SomeStore.Save", log);                 // the call-site label is greppable
        Assert.Contains("InvalidOperationException", log);      // the exception type survives
        Assert.Contains("boom", log);                           // ...and its message
    }

    [Fact]
    public void Swallowed_appends_rather_than_replacing()
    {
        Diagnostics.Swallowed(new Exception("first"), "A");
        Diagnostics.Swallowed(new Exception("second"), "B");

        string log = File.ReadAllText(Diagnostics.FilePath);
        Assert.Contains("first", log);
        Assert.Contains("second", log);
    }

    [Fact]
    public void Swallowed_rolls_the_log_once_it_passes_the_cap()
    {
        // The cap is 128 KB; each entry carries the full exception, so a few hundred are plenty to cross it.
        for (int i = 0; i < 400; i++)
            Diagnostics.Swallowed(new Exception(new string('x', 1024)), $"Site.{i}");

        // A previous generation was rolled off, and the live file stayed bounded rather than growing forever.
        Assert.True(File.Exists(Diagnostics.FilePath + ".1"));
        Assert.True(new FileInfo(Diagnostics.FilePath).Length <= 256 * 1024);
    }

    [Fact]
    public void Swallowed_never_throws_even_when_the_sink_cannot_write()
    {
        // Point the state directory at a path that can't be a directory (a file already sits there), so the
        // sink's own write fails — it must swallow that too rather than fault the caller.
        string filePath = Path.Combine(_dir, "not-a-dir");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(filePath, "x");
        StatusFile.OverrideDirectory(filePath);

        Diagnostics.Swallowed(new Exception("boom"), "Wherever"); // must not throw
    }
}
