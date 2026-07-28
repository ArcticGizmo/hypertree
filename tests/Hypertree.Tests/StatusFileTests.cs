using Hypertree.Status;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the published file itself: that a reader gets back exactly what the tray wrote, and that the
/// three ways a reader must decline — no file, a file from a dead tray, a schema it predates — all read
/// as "no status" rather than as something to render.
/// </summary>
/// <remarks>
/// Redirected to a temp directory (see <c>StatusFile.OverrideDirectory</c>) so the suite never touches the
/// real <c>%APPDATA%</c> file, which a running tray owns.
/// </remarks>
[Collection(StatusFileCollection.Name)] // shares the process-global StatusFile.OverrideDirectory — run serially
public sealed class StatusFileTests : IDisposable
{
    private readonly string _dir;

    public StatusFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N"));
        StatusFile.OverrideDirectory(_dir);
    }

    public void Dispose()
    {
        StatusFile.OverrideDirectory(null);
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static StatusSnapshot Sample() => new()
    {
        Version = "0.1.5",
        Pid = Environment.ProcessId, // alive, so Read accepts it
        Cli = @"C:\tools\htree.exe",
        Rows =
        {
            new StatusRow
            {
                Kind = RowKind.Branch,
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Name = "perch",
                Cursor = 1,
                Desktops =
                {
                    new StatusDesktop { Id = Guid.NewGuid(), Label = "code" },
                    new StatusDesktop { Id = Guid.NewGuid(), Label = "docs" },
                },
            },
            new StatusRow
            {
                Kind = RowKind.Main,
                Name = "main",
                Cursor = 0,
                Desktops = { new StatusDesktop { Id = Guid.NewGuid(), Label = "Desktop 1" } },
            },
        },
        Current = new StatusPosition { Row = 0, Desktop = 1 },
    };

    [Fact]
    public void What_the_tray_writes_is_what_a_reader_gets_back()
    {
        StatusFile.Write(Sample());

        StatusSnapshot? read = StatusFile.Read();
        Assert.NotNull(read);
        Assert.Equal(StatusFile.SchemaVersion, read!.Schema);
        Assert.Equal("0.1.5", read.Version);
        Assert.Equal(@"C:\tools\htree.exe", read.Cli);
        Assert.Equal(new[] { "perch", "main" }, read.Rows.Select(r => r.Name));
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), read.Rows[0].Id);
        Assert.True(read.Rows[1].IsMain);
        Assert.Equal("docs", read.CurrentDesktop!.Label);
        Assert.Equal("perch", read.CurrentRow!.Name);
    }

    [Fact]
    public void No_file_means_no_status()
    {
        Assert.Null(StatusFile.Read()); // nothing written yet — a tray has never run here
    }

    [Fact]
    public void A_file_left_by_a_dead_tray_is_ignored()
    {
        // A clean exit deletes the file, but a kill or a crash can't. Trusting mere existence would show
        // a live current-desktop marker for a tray that isn't there.
        StatusSnapshot snapshot = Sample();
        snapshot.Pid = DeadPid();
        StatusFile.Write(snapshot);

        Assert.Null(StatusFile.Read());
    }

    [Fact]
    public void A_schema_the_reader_predates_is_refused_rather_than_guessed_at()
    {
        StatusSnapshot snapshot = Sample();
        snapshot.Schema = StatusFile.SchemaVersion + 1;
        StatusFile.Write(snapshot);

        Assert.Null(StatusFile.Read());
    }

    [Fact]
    public void Malformed_content_reads_as_no_status_rather_than_throwing()
    {
        File.WriteAllText(StatusFile.FilePath, "{ this is not json");
        Assert.Null(StatusFile.Read()); // a reader must never be taken down by a bad file
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        StatusFile.Write(Sample());
        Assert.NotNull(StatusFile.Read());

        StatusFile.Delete();
        Assert.Null(StatusFile.Read());
    }

    [Fact]
    public void Writing_leaves_no_temp_file_behind()
    {
        // The write is temp-then-replace so readers never see a half-written file; the temp must not
        // survive, or the state directory silently accumulates one per session.
        StatusFile.Write(Sample());

        Assert.Empty(System.IO.Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(System.IO.Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_live_process_reads_as_alive_and_an_absent_one_does_not()
    {
        Assert.True(StatusFile.IsAlive(Environment.ProcessId));
        Assert.False(StatusFile.IsAlive(DeadPid()));
        Assert.False(StatusFile.IsAlive(0));
        Assert.False(StatusFile.IsAlive(-1));
    }

    // A pid that is almost certainly not in use: above the default Windows range and never allocated here.
    private static int DeadPid()
    {
        for (int candidate = 0x3FFF_FFFF; candidate > 0x3FFF_FF00; candidate--)
            if (!StatusFile.IsAlive(candidate)) return candidate;
        throw new InvalidOperationException("Could not find an unused pid.");
    }
}
