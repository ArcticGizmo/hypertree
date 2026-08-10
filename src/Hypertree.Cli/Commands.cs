using System.Text.Json;
using Hypertree.Ipc;
using Hypertree.Status;

namespace Hypertree.Cli;

/// <summary>
/// The commands <c>htree</c> offers. Each returns the process exit code, so the shell always learns what
/// happened even when output is suppressed or piped away. The status source, control transport and output
/// are injected (<see cref="IStatusSource"/> / <see cref="IControlTransport"/> / <see cref="ICliOutput"/>)
/// so the formatting and exit-code logic is unit-testable without spawning a process.
/// </summary>
internal sealed class Commands
{
    private readonly IStatusSource _status;
    private readonly IControlTransport _control;
    private readonly ICliOutput _out;

    public Commands(IStatusSource status, IControlTransport control, ICliOutput output)
    {
        _status = status;
        _control = control;
        _out = output;
    }

    // ── status ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the cursor is, in one line — the command intended to sit in a shell prompt.
    /// </summary>
    /// <remarks>
    /// Silence plus a non-zero exit when no tray is running is the deliberate behaviour: a prompt that
    /// embeds this should show nothing at all when Hypertree isn't up, not an error or a placeholder.
    /// </remarks>
    public int Status(Args args)
    {
        StatusSnapshot? status = _status.Read();
        if (status is null) return ExitCode.NoTray; // quiet on purpose — see remarks

        if (args.Json)
        {
            _out.Line(Json(status));
            return ExitCode.Ok;
        }

        StatusRow? row = status.CurrentRow;
        StatusDesktop? desktop = status.CurrentDesktop;
        if (row is null) return ExitCode.NoTray;

        string branch = row.Name;
        string label = desktop?.Label ?? "";

        if (args.Has("--branch")) { _out.Line(branch); return ExitCode.Ok; }
        if (args.Has("--desktop")) { _out.Line(label); return ExitCode.Ok; }

        _out.Line(label.Length == 0 ? branch : $"{_out.Paint(branch, Output.Cyan)}/{label}");
        return ExitCode.Ok;
    }

    // ── list ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stack, top to bottom, main in its slot.
    /// </summary>
    /// <remarks>
    /// The default view shows one line per row ending at that row's resume desktop — that is, at exactly
    /// what <c>htree goto &lt;row&gt;</c> would land on. Showing the same thing the jump would do makes
    /// the two commands legible together, rather than making the reader hold the resume rule in their head.
    /// <c>--all</c> expands the desktops for when the whole layout is the question.
    /// </remarks>
    public int List(Args args)
    {
        StatusSnapshot? status = _status.Read();
        if (status is null)
        {
            _out.Error("No Hypertree tray is running.");
            return ExitCode.NoTray;
        }

        if (args.Json)
        {
            _out.Line(Json(status));
            return ExitCode.Ok;
        }

        bool all = args.Has("--all") || args.Has("-a");
        int width = status.Rows.Count == 0 ? 4 : Math.Max(4, status.Rows.Max(r => r.Name.Length));

        for (int i = 0; i < status.Rows.Count; i++)
        {
            StatusRow row = status.Rows[i];
            bool here = i == status.Current.Row;
            string marker = here ? _out.Paint("*", Output.Cyan) : " ";
            string name = here ? _out.Paint(row.Name.PadRight(width), Output.Bold) : row.Name.PadRight(width);
            string resume = row.Cursor >= 0 && row.Cursor < row.Desktops.Count ? row.Desktops[row.Cursor].Label : "";
            string count = _out.Paint($"{row.Desktops.Count} desktop{(row.Desktops.Count == 1 ? "" : "s")}", Output.Dim);

            _out.Line($"{marker} {name}  {resume,-24} {count}");

            if (!all) continue;
            for (int d = 0; d < row.Desktops.Count; d++)
            {
                bool onThis = here && d == status.Current.Desktop;
                string bullet = onThis ? _out.Paint("→", Output.Cyan) : " ";
                // 1-based, because this is the number `htree goto row/N` takes.
                _out.Line($"    {bullet} {d + 1}. {row.Desktops[d].Label}");
            }
        }

        return ExitCode.Ok;
    }

    // ── goto ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Jump to a row, or to a specific desktop on it.</summary>
    public int Goto(Args args)
    {
        string? target = args.Positional.ElementAtOrDefault(1);
        string? idFlag = args.Value("--id");

        if (target is null && idFlag is null)
        {
            _out.Error("goto needs a target. Try: htree goto <branch>|main|<branch>/<desktop>");
            return ExitCode.BadUsage;
        }

        StatusSnapshot? status = _status.Read();
        if (status is null)
        {
            _out.Error("No Hypertree tray is running.");
            return ExitCode.NoTray;
        }

        TargetResolution resolved;
        if (idFlag is not null)
        {
            if (!Guid.TryParse(idFlag, out Guid id))
            {
                _out.Error($"--id expects a branch id, got '{idFlag}'.");
                return ExitCode.BadUsage;
            }
            // A desktop can still be named alongside an explicit id.
            resolved = Targets.Resolve(status, target is null ? id.ToString() : $"{id}/{target}");
        }
        else
        {
            resolved = Targets.Resolve(status, target!);
        }

        if (!resolved.Ok)
        {
            _out.Error(resolved.Error!);
            return ExitCode.UnknownTarget;
        }

        ControlResponse response = _control.Send(new ControlRequest
        {
            Command = ControlRequest.CommandGoto,
            Goto = new GotoRequest { BranchId = resolved.BranchId, Desktop = resolved.Desktop },
        });

        if (!response.Ok)
        {
            _out.Error(response.Error ?? "The jump failed.");
            return response.Code;
        }

        // Quiet by default on success — a jump you can see happen doesn't need narrating, and silence
        // keeps it usable inside other commands. --verbose says where it went.
        if (args.Has("--verbose") || args.Has("-v")) _out.Line(response.Landed ?? "");
        return ExitCode.Ok;
    }

    // ── watch ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stream position changes, one line each, until interrupted.
    /// </summary>
    /// <remarks>
    /// Watches the status file rather than polling the tray, so it costs nothing while nothing is
    /// happening — and because the tray keeps that file true even for switches Hypertree didn't make, this
    /// reports Win+Ctrl+Arrow and Task View too, not just Hypertree's own navigation.
    /// </remarks>
    public int Watch(Args args)
    {
        using var watcher = new FileSystemWatcher(StatusFile.Directory, StatusFile.FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        using var signal = new AutoResetEvent(false);
        void Wake(object? _, FileSystemEventArgs __) => signal.Set();
        watcher.Changed += Wake;
        watcher.Created += Wake;
        watcher.Deleted += Wake;
        watcher.Renamed += Wake;

        using var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); signal.Set(); };

        string last = "";
        Emit(args, ref last); // report the current position immediately, so the stream starts populated

        while (!quit.IsCancellationRequested)
        {
            signal.WaitOne(TimeSpan.FromSeconds(1)); // the timeout also catches a tray exiting unnoticed
            if (quit.IsCancellationRequested) break;
            // The write is a create-and-replace, so a single update can raise several events; settling
            // briefly coalesces them into the one line the user actually wants.
            Thread.Sleep(30);
            Emit(args, ref last);
        }

        return ExitCode.Ok;
    }

    private void Emit(Args args, ref string last)
    {
        StatusSnapshot? status = _status.Read();
        string line = status is null
            ? "" // no tray
            : args.Json
                ? JsonCompact(status)
                : $"{status.CurrentRow?.Name ?? ""}/{status.CurrentDesktop?.Label ?? ""}";

        if (line == last) return;
        last = line;
        if (status is null) _out.Error("Hypertree stopped.");
        else _out.Line(line);
        Console.Out.Flush(); // a consumer piping this wants each line as it happens, not at buffer-fill
    }

    // ── shared ───────────────────────────────────────────────────────────────────────────────────

    private static string Json(StatusSnapshot status)
        => JsonSerializer.Serialize(status, StatusJson.Indented);

    private static string JsonCompact(StatusSnapshot status)
        => JsonSerializer.Serialize(status, StatusJson.Compact);
}
