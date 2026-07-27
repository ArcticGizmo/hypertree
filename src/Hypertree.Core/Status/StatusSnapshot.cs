using System.Text.Json.Serialization;

namespace Hypertree.Status;

/// <summary>
/// Hypertree's layout and position as published to the outside world — what <c>htree</c> reads, and what
/// any other tool (Perch's overlay strip, a shell prompt, a status bar) can watch.
/// </summary>
/// <remarks>
/// <para><b>Why a file and not a query.</b> Readers want this continuously and cheaply — a shell prompt
/// renders it on every command, an overlay keeps a live marker on it. A process spawn or an IPC round trip
/// per read is far too expensive for that, whereas a small file can be watched. Only the one mutating
/// action (<c>goto</c>) needs to reach the tray, and that goes over the control pipe.</para>
///
/// <para><b>Why rows, rather than main + branches.</b> The vertical stack is
/// <c>branches[0..mainSlot-1] / MAIN / branches[mainSlot..]</c>, and main is not a branch — it has no
/// entry in the model's branch list. Publishing that shape would make every reader re-derive the draw
/// order and synthesise a row for main. <see cref="Rows"/> is instead the stack already flattened
/// top-to-bottom with main in its slot, so a reader renders the array as it stands and a reorder is
/// simply a different order.</para>
/// </remarks>
public sealed class StatusSnapshot
{
    /// <summary>Contract version. Bumped only for a breaking change; readers should refuse a schema they
    /// don't know rather than guess at it.</summary>
    public int Schema { get; set; } = StatusFile.SchemaVersion;

    /// <summary>The running Hypertree's product version, so a reader can tell what it's talking to.</summary>
    public string Version { get; set; } = "";

    /// <summary>The tray's process id. A reader checks this is alive to catch a file left behind by a
    /// crash — the clean-exit path deletes it, but a kill can't.</summary>
    public int Pid { get; set; }

    /// <summary>Absolute path to <c>htree.exe</c> as shipped beside the running tray, or null if it isn't
    /// there. Saves every reader from guessing at an install layout to find the CLI.</summary>
    public string? Cli { get; set; }

    /// <summary>The vertical stack, top to bottom, main included at its slot.</summary>
    public List<StatusRow> Rows { get; set; } = new();

    /// <summary>Where the cursor actually is — kept true by the ambient desktop watcher, so it stays
    /// correct after a switch made outside Hypertree.</summary>
    public StatusPosition Current { get; set; } = new();

    /// <summary>The row the cursor is on, or null if <see cref="Current"/> points outside <see cref="Rows"/>.</summary>
    [JsonIgnore]
    public StatusRow? CurrentRow
        => Current.Row >= 0 && Current.Row < Rows.Count ? Rows[Current.Row] : null;

    /// <summary>The desktop the cursor is on, or null if the position doesn't resolve.</summary>
    [JsonIgnore]
    public StatusDesktop? CurrentDesktop
        => CurrentRow is { } r && Current.Desktop >= 0 && Current.Desktop < r.Desktops.Count
            ? r.Desktops[Current.Desktop]
            : null;
}

/// <summary>One row of the stack: the main timeline, or a branch.</summary>
public sealed class StatusRow
{
    /// <summary><c>"main"</c> or <c>"branch"</c>.</summary>
    public string Kind { get; set; } = RowKind.Branch;

    /// <summary>The branch's stable id. Null for main, which has no identity to carry — it is addressed
    /// as <c>main</c>.</summary>
    public Guid? Id { get; set; }

    /// <summary>The branch's name, or <c>"main"</c> for the main timeline. Not guaranteed unique across
    /// branches — resolve by <see cref="Id"/> when it matters.</summary>
    public string Name { get; set; } = "";

    /// <summary>The row's resume point: the desktop index a jump to this row lands on.</summary>
    public int Cursor { get; set; }

    public List<StatusDesktop> Desktops { get; set; } = new();

    /// <summary>Convenience over <see cref="Kind"/>. Not serialised — it is derived, and publishing it
    /// would invite a reader to trust a second, redundant source of the same fact.</summary>
    [JsonIgnore]
    public bool IsMain => Kind == RowKind.Main;
}

/// <summary>One desktop within a row.</summary>
public sealed class StatusDesktop
{
    /// <summary>The OS virtual-desktop GUID. Published so a script can correlate with anything else that
    /// speaks the same ids; Hypertree's own callers address desktops by position.</summary>
    public Guid Id { get; set; }

    public string Label { get; set; } = "";
}

/// <summary>Indices into <see cref="StatusSnapshot.Rows"/> and that row's desktops.</summary>
public sealed class StatusPosition
{
    public int Row { get; set; }
    public int Desktop { get; set; }
}

public static class RowKind
{
    public const string Main = "main";
    public const string Branch = "branch";
}
