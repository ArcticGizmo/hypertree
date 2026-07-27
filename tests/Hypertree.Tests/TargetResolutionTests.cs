using Hypertree.Cli;
using Hypertree.Status;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers how <c>htree</c> turns what someone types into the id the tray acts on: the forgiving match
/// order, 1-based desktop positions, and the cases it must refuse rather than guess at.
/// </summary>
public class TargetResolutionTests
{
    private static readonly Guid PerchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NotesId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static StatusSnapshot Status() => new()
    {
        Rows =
        {
            Branch(PerchId, "perch", cursor: 1, "code", "docs"),
            new StatusRow
            {
                Kind = RowKind.Main, Name = "main", Cursor = 0,
                Desktops = { Desktop("Desktop 1"), Desktop("Desktop 2") },
            },
            Branch(NotesId, "notes", cursor: 0, "inbox"),
        },
        Current = new StatusPosition { Row = 1, Desktop = 0 },
    };

    private static StatusRow Branch(Guid id, string name, int cursor, params string[] labels) => new()
    {
        Kind = RowKind.Branch,
        Id = id,
        Name = name,
        Cursor = cursor,
        Desktops = labels.Select(Desktop).ToList(),
    };

    private static StatusDesktop Desktop(string label) => new() { Id = Guid.NewGuid(), Label = label };

    [Fact]
    public void A_branch_name_resolves_to_its_id_and_leaves_the_desktop_to_the_tray()
    {
        TargetResolution r = Targets.Resolve(Status(), "perch");

        Assert.True(r.Ok);
        Assert.Equal(PerchId, r.BranchId);
        Assert.Null(r.Desktop); // null means "the row's resume point" — the tray decides, not us
    }

    [Fact]
    public void Main_resolves_to_a_null_branch()
    {
        TargetResolution r = Targets.Resolve(Status(), "main");

        Assert.True(r.Ok);
        Assert.Null(r.BranchId); // main is not a branch and has no id
        Assert.Null(r.Desktop);
    }

    [Theory]
    [InlineData("PERCH")]
    [InlineData("Perch")]
    [InlineData("pe")]     // unique prefix
    [InlineData("p")]      // still unique — only one row starts with p
    public void Names_match_case_insensitively_and_by_unique_prefix(string typed)
    {
        TargetResolution r = Targets.Resolve(Status(), typed);

        Assert.True(r.Ok);
        Assert.Equal(PerchId, r.BranchId);
    }

    [Fact]
    public void A_desktop_can_be_named_by_label()
    {
        TargetResolution r = Targets.Resolve(Status(), "perch/docs");

        Assert.True(r.Ok);
        Assert.Equal(PerchId, r.BranchId);
        Assert.Equal(1, r.Desktop);
    }

    [Fact]
    public void A_desktop_can_be_named_by_one_based_position()
    {
        // 1-based because that's what `htree list --all` prints; the wire index is 0-based.
        TargetResolution r = Targets.Resolve(Status(), "perch/2");

        Assert.True(r.Ok);
        Assert.Equal(1, r.Desktop);
    }

    [Fact]
    public void A_desktop_label_wins_over_a_position_of_the_same_text()
    {
        // A desktop literally named "2" must stay reachable as "2", so labels are matched first.
        var status = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "perch", 0, "first", "2", "third") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };

        TargetResolution r = Targets.Resolve(status, "perch/2");

        Assert.True(r.Ok);
        Assert.Equal(1, r.Desktop); // the desktop labelled "2", not position 2 (which would be index 1 too)

        // Prove the label really won, using a label whose position differs from its text.
        var other = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "perch", 0, "3", "b", "c") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };
        Assert.Equal(0, Targets.Resolve(other, "perch/3").Desktop); // label "3" at index 0, not position 3
    }

    [Fact]
    public void A_desktop_label_containing_a_slash_still_resolves()
    {
        // The split is on the LAST slash, so a row name stays a single token and labels may contain one.
        var status = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "perch", 0, "a", "ui/spec") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };

        TargetResolution r = Targets.Resolve(status, "perch/ui/spec");

        Assert.True(r.Ok);
        Assert.Equal(1, r.Desktop);
    }

    [Fact]
    public void An_ambiguous_name_is_refused_and_says_which_rows_collided()
    {
        var status = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "dup", 0, "a"), Branch(NotesId, "dup", 0, "b") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };

        TargetResolution r = Targets.Resolve(status, "dup");

        Assert.False(r.Ok);
        Assert.True(r.Ambiguous);
        Assert.Contains(PerchId.ToString(), r.Error);
        Assert.Contains(NotesId.ToString(), r.Error); // both ids offered, so the user can pick one
    }

    [Fact]
    public void An_ambiguous_prefix_is_refused_rather_than_picking_the_first()
    {
        var status = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "note", 0, "a"), Branch(NotesId, "notes", 0, "b") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };

        // "not" prefixes both. An exact name would win outright — but this isn't one.
        Assert.False(Targets.Resolve(status, "not").Ok);
        // …whereas the exact name resolves, even though it also prefixes the other.
        Assert.Equal(PerchId, Targets.Resolve(status, "note").BranchId);
    }

    [Fact]
    public void An_explicit_id_resolves_and_is_never_ambiguous()
    {
        var status = new StatusSnapshot
        {
            Rows = { Branch(PerchId, "dup", 0, "a"), Branch(NotesId, "dup", 0, "b") },
            Current = new StatusPosition { Row = 0, Desktop = 0 },
        };

        TargetResolution r = Targets.Resolve(status, NotesId.ToString());

        Assert.True(r.Ok);
        Assert.Equal(NotesId, r.BranchId);
    }

    [Fact]
    public void An_unknown_name_is_refused_with_something_to_try()
    {
        TargetResolution r = Targets.Resolve(Status(), "nope");

        Assert.False(r.Ok);
        Assert.False(r.Ambiguous);
        Assert.Contains("htree list", r.Error);
    }

    [Fact]
    public void An_out_of_range_position_is_refused_and_lists_what_is_there()
    {
        TargetResolution r = Targets.Resolve(Status(), "perch/9");

        Assert.False(r.Ok);
        Assert.Contains("1:code", r.Error);
        Assert.Contains("2:docs", r.Error);
    }

    [Fact]
    public void An_empty_target_is_refused()
    {
        Assert.False(Targets.Resolve(Status(), "").Ok);
        Assert.False(Targets.Resolve(Status(), "   ").Ok);
    }
}
