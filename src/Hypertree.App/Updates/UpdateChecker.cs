using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Hypertree.App.Updates;

/// <summary>Outcome of an update check.</summary>
public enum UpdateAvailability
{
    /// <summary>No feed configured, or the app wasn't installed via Velopack (e.g. a dev build).</summary>
    NotApplicable,
    /// <summary>Already on the latest release.</summary>
    UpToDate,
    /// <summary>A newer release is available (see <see cref="UpdateCheckResult.AvailableVersion"/>).</summary>
    Available,
    /// <summary>The check failed (offline, bad feed, …). Never fatal.</summary>
    Failed,
}

/// <summary>
/// The result of an update check. Carries the Velopack handles needed to <b>apply</b> the update, so
/// the surface that showed the "update available" card can download + install without re-checking.
/// </summary>
public sealed class UpdateCheckResult
{
    public required UpdateAvailability Availability { get; init; }
    public string? CurrentVersion { get; init; }
    public string? AvailableVersion { get; init; }

    internal UpdateManager? Manager { get; init; }
    internal UpdateInfo? Info { get; init; }
}

/// <summary>
/// Checks a release feed for a newer version, and applies it on request. Checking and applying are both
/// explicit, user-driven actions (from the tray, the command palette, or Settings) — there is no
/// automatic launch-time check.
/// </summary>
/// <remarks>
/// By default the feed is the GitHub Releases of <see cref="DefaultRepoUrl"/> (the tag-triggered
/// <c>release.yml</c> publishes the Velopack packages there). The <c>HYPERTREE_UPDATE_FEED</c>
/// environment variable overrides that with a directory path or URL — handy for testing against a local
/// releases folder. Either way, if the app wasn't installed via Velopack (e.g. run from the build
/// output) the check reports <see cref="UpdateAvailability.NotApplicable"/>. Any failure is swallowed
/// into <see cref="UpdateAvailability.Failed"/> — a flaky feed must never throw into the UI.
/// </remarks>
public static class UpdateChecker
{
    public const string FeedEnvVar = "HYPERTREE_UPDATE_FEED";

    /// <summary>The default release feed: the GitHub repo whose Releases the pipeline publishes to.</summary>
    public const string DefaultRepoUrl = "https://github.com/ArcticGizmo/hypertree";

    // The feed to check: the HYPERTREE_UPDATE_FEED override (a local dir / URL) when set, else the
    // GitHub Releases of DefaultRepoUrl. Unauthenticated GitHub requests (null token) are enough for a
    // public repo; stable releases only (no prereleases).
    private static UpdateManager BuildManager()
    {
        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
        return string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(DefaultRepoUrl, accessToken: null, prerelease: false, downloader: null))
            : new UpdateManager(feed);
    }

    /// <summary>
    /// Checks the feed for a newer release. Never throws; returns the <see cref="UpdateAvailability"/>
    /// plus the handles required to apply an available update.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckDetailedAsync()
    {
        try
        {
            var manager = BuildManager();
            if (!manager.IsInstalled)
                return new UpdateCheckResult { Availability = UpdateAvailability.NotApplicable };

            var current = manager.CurrentVersion?.ToString();
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return new UpdateCheckResult { Availability = UpdateAvailability.UpToDate, CurrentVersion = current };

            return new UpdateCheckResult
            {
                Availability = UpdateAvailability.Available,
                CurrentVersion = current,
                AvailableVersion = update.TargetFullRelease.Version.ToString(),
                Manager = manager,
                Info = update,
            };
        }
        catch
        {
            return new UpdateCheckResult { Availability = UpdateAvailability.Failed };
        }
    }

    /// <summary>
    /// Downloads and installs the update described by <paramref name="result"/>, then restarts the app.
    /// Only valid when <see cref="UpdateCheckResult.Availability"/> is
    /// <see cref="UpdateAvailability.Available"/>. Does not return on success (the process restarts).
    /// </summary>
    public static async Task ApplyAsync(UpdateCheckResult result)
    {
        if (result is not { Availability: UpdateAvailability.Available, Manager: { } manager, Info: { } info })
            return;

        await manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }
}
