using Hypertree.Changelog;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the changelog diff logic: sections split on <c>## </c> headings, versions parsed out of the
/// <c>[vX.Y.Z]</c> headings, and <see cref="ChangelogParser.UnseenSince"/> returning exactly the releases
/// between the last-seen and current versions (exclusive of last-seen, inclusive of current).
/// </summary>
public class ChangelogParserTests
{
    private const string Sample = """
        # Changelog

        ## [Unreleased]

        - work in progress

        ---

        ## [v0.2.0] - 2026-07-26

        - the second thing

        ---

        ## [v0.1.0] - 2026-07-20

        - the first thing
        """;

    [Fact]
    public void Parse_splits_every_heading_and_reads_versions()
    {
        var sections = ChangelogParser.Parse(Sample);

        Assert.Equal(3, sections.Count);
        Assert.Null(sections[0].Version);                    // [Unreleased] is unversioned
        Assert.Equal(new Version("0.2.0"), sections[1].Version);
        Assert.Equal("v0.2.0", sections[1].Display);
        Assert.Equal(new Version("0.1.0"), sections[2].Version);
    }

    [Fact]
    public void UnseenSince_returns_only_releases_newer_than_last_seen()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, lastSeen: "v0.1.0", current: "v0.2.0");

        var single = Assert.Single(unseen);
        Assert.Equal(new Version("0.2.0"), single.Version);   // 0.2.0 only — 0.1.0 excluded, Unreleased skipped
    }

    [Fact]
    public void UnseenSince_is_empty_on_a_fresh_install()
    {
        // No last-seen version (fresh install) means there's no history to diff against.
        Assert.Empty(ChangelogParser.UnseenSince(Sample, lastSeen: null, current: "v0.2.0"));
    }

    [Fact]
    public void UnseenSince_is_empty_when_already_on_the_current_version()
    {
        Assert.Empty(ChangelogParser.UnseenSince(Sample, lastSeen: "v0.2.0", current: "v0.2.0"));
    }
}
