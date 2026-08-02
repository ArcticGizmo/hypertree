using System.Text.Json.Serialization.Metadata;

namespace Hypertree.Ipc;

/// <summary>
/// The request/response vocabulary spoken over Hypertree's control pipe, and the one place its name and
/// exit codes are defined — so the tray (server) and <c>htree</c> (client) cannot drift apart.
/// </summary>
/// <remarks>
/// <para><b>Why a pipe at all.</b> Virtual-desktop control is per-session COM plus the tray's own in-memory
/// bookkeeping, and <c>SingleInstance</c> exists precisely because two processes driving it would fight. So
/// a <c>htree goto</c> process cannot perform a jump itself — it has to hand the request to the tray. The
/// existing activation event can't carry it: it's a bare auto-reset signal with nowhere to put "which
/// branch", and it already means "surface your palette".</para>
///
/// <para><b>Why a pipe rather than another signal.</b> A pipe carries a payload <em>and</em> a reply, which
/// is what turns "did it work?" into a real exit code instead of a silent no-op. It also fails usefully:
/// no pipe to connect to <em>is</em> the "no tray running" answer, with no liveness guesswork.</para>
///
/// <para><b>Addressing.</b> Requests name a branch by <see cref="GotoRequest.BranchId"/>, never by list
/// index — a caller that read the layout, then acted on a position, could land on a branch the user
/// reordered in between. Name resolution happens client-side, where an ambiguous name can be reported to
/// the human who typed it; the tray only ever receives an unambiguous id.</para>
///
/// <para>The namespace is <c>Ipc</c> rather than the more natural <c>Control</c> because this assembly's
/// root namespace is <c>Hypertree</c>, and a <c>Hypertree.Control</c> namespace would shadow Avalonia's
/// <c>Control</c> type for every unqualified use in the app's views.</para>
/// </remarks>
public static class ControlProtocol
{
    /// <summary>
    /// The control pipe's name, scoped to the logon session. Virtual desktops are per-session and the
    /// single-instance guard is <c>Local\</c>-scoped for the same reason, but named pipes have no
    /// <c>Local\</c> equivalent — the namespace is machine-wide — so the session id goes in the name by
    /// hand. Without it, two users switched between on one machine would collide on the pipe, and a CLI
    /// could reach the wrong session's tray.
    /// </summary>
    public static string PipeName
    {
        get
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return $"Hypertree.Control.{self.SessionId}";
        }
    }

    /// <summary>How long the client waits to connect before calling it "no tray running". Generous enough
    /// to cover a tray busy on its UI thread, short enough that a human doesn't wonder if it hung.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>How long the client waits for a reply once connected. A jump is a couple of COM calls;
    /// anything beyond this means the tray is wedged, and reporting that beats hanging a shell.</summary>
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(5000);

    // Source-generated type info rather than a JsonSerializerOptions, because only the JsonTypeInfo
    // overloads of Serialize/Deserialize are free of the reflection that trimming and AOT can't follow.
    // See IpcJsonContext.
    public static JsonTypeInfo<ControlRequest> RequestInfo => IpcJsonContext.Default.ControlRequest;
    public static JsonTypeInfo<ControlResponse> ResponseInfo => IpcJsonContext.Default.ControlResponse;
}

/// <summary>Process exit codes. Stable contract — scripts and other tools branch on these.</summary>
public static class ExitCode
{
    public const int Ok = 0;
    /// <summary>No Hypertree tray is running in this session.</summary>
    public const int NoTray = 1;
    /// <summary>The branch, desktop or row asked for doesn't exist (or the name was ambiguous).</summary>
    public const int UnknownTarget = 2;
    /// <summary>The command line didn't parse.</summary>
    public const int BadUsage = 3;
    /// <summary>The tray was reached but couldn't carry out the request.</summary>
    public const int Failed = 4;
}

/// <summary>A single request. One command per connection; the pipe is not kept open.</summary>
public sealed class ControlRequest
{
    public string Command { get; set; } = "";

    /// <summary>Populated when <see cref="Command"/> is <c>goto</c>.</summary>
    public GotoRequest? Goto { get; set; }

    /// <summary>Populated when <see cref="Command"/> is <c>populate</c>.</summary>
    public PopulateRequest? Populate { get; set; }

    public const string CommandGoto = "goto";
    public const string CommandPing = "ping";
    public const string CommandPopulate = "populate";
}

/// <summary>Apply a named loadout as a new branch, supplying its <c>{name}</c> variable values. The client
/// always sends the built-in <c>dir</c> (its current working directory); the tray fills the rest from the
/// values given, then their declared defaults, and prompts for anything still missing.</summary>
public sealed class PopulateRequest
{
    public string Name { get; set; } = "";

    /// <summary>Variable values, keyed by variable name (case matched loosely by the tray). Includes
    /// <c>dir</c> = the caller's working directory, plus any the caller passed explicitly.</summary>
    public Dictionary<string, string> Values { get; set; } = new();
}

/// <summary>Jump to a row's resume desktop, or to a specific desktop on it.</summary>
public sealed class GotoRequest
{
    /// <summary>The branch to jump to. Null means the main timeline.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>A specific desktop index within the row. Null means the row's remembered cursor — its
    /// resume point — which is what a bare "go to this branch" means.</summary>
    public int? Desktop { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; }

    /// <summary>The <see cref="ExitCode"/> the client should exit with.</summary>
    public int Code { get; set; }

    /// <summary>Human-readable failure detail, for stderr. Null on success.</summary>
    public string? Error { get; set; }

    /// <summary>Where the tray ended up, for a success message ("perch/docs"). Null when nothing moved.</summary>
    public string? Landed { get; set; }

    public static ControlResponse Success(string? landed = null)
        => new() { Ok = true, Code = ExitCode.Ok, Landed = landed };

    public static ControlResponse Failure(int code, string error)
        => new() { Ok = false, Code = code, Error = error };
}
