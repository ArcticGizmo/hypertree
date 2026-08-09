using Avalonia.Controls;
using Avalonia.Threading;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Platform;

namespace Hypertree.App;

/// <summary>
/// Owns the software-update flow (tray · command palette · settings): check, remember the result, and apply
/// it (which restarts the app on success). Deliberately stays out of the way — nothing opens over the
/// desktop and nothing takes the pointer; the whole flow reports through Action Center notifications, the
/// tray's update entry (which this owns and retitles), and the Settings window's inline status.
/// </summary>
/// <remarks>
/// The notifier and the Settings window are read through accessors rather than captured: the notifier is
/// built after this controller (see App.Startup) and the Settings window comes and goes, so both must be
/// read live at call time. The command palette reads <see cref="UpdateReady"/>/<see cref="Last"/> live to
/// offer "Update now — vX" vs "Check for updates".
/// </remarks>
internal sealed class UpdateController
{
    // The one action an update notification carries: "the update you were told about — install it". The app
    // shell wires INotifier.Activated and routes this action back to ApplyLast.
    public const string ApplyAction = "update";
    // Every step of the flow reuses this key, so checking → result → downloading updates one notification in
    // place instead of leaving three behind in the Action Center.
    private const string NoticeKey = "update-check";

    private readonly Func<INotifier?> _notifier;
    private readonly Func<SettingsWindow?> _settingsWindow;
    private readonly Action _dismissOverlay;

    // The most recent check, remembered so the tray and palette can offer "Update now" directly (skipping a
    // re-check) once a newer release has been found — even if the notification was never seen.
    private UpdateCheckResult? _last;
    private NativeMenuItem? _menuItem;

    public UpdateController(Func<INotifier?> notifier, Func<SettingsWindow?> settingsWindow, Action dismissOverlay)
    {
        _notifier = notifier;
        _settingsWindow = settingsWindow;
        _dismissOverlay = dismissOverlay;
    }

    /// <summary>The most recent check result, or null if none yet — the command palette reads this to offer
    /// "Update now" directly.</summary>
    public UpdateCheckResult? Last => _last;

    /// <summary>True when the last check found an installable newer release.</summary>
    public bool UpdateReady => _last is { Availability: UpdateAvailability.Available };

    /// <summary>Create the tray's update entry (held so a finished check can retitle it) and wire its click.</summary>
    public NativeMenuItem CreateMenuItem()
    {
        _menuItem = new NativeMenuItem("Check for updates");
        _menuItem.Click += (_, _) => Dispatcher.UIThread.Post(RunMenuItem);
        return _menuItem;
    }

    // The tray entry does whatever its title says — apply a found release, else check. Reading _last here
    // (rather than trusting the title) keeps the two from drifting.
    public void RunMenuItem()
    {
        if (UpdateReady) ApplyLast();
        else Check();
    }

    // Entry point for the tray item, the command palette, and the Settings window's button.
    public void Check()
    {
        _settingsWindow()?.OnUpdateCheckStarted();
        // Silent: the result notification follows moments later, and a check shouldn't chime twice.
        Notify("Checking for updates…", "Looking for a newer release of Hypertree.", silent: true);
        _ = ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        UpdateCheckResult result;
        try { result = await UpdateChecker.CheckDetailedAsync().ConfigureAwait(false); }
        catch { result = new UpdateCheckResult { Availability = UpdateAvailability.Failed }; }

        OnUi(() =>
        {
            _last = result;
            RefreshMenuItem();
            _settingsWindow()?.OnUpdateResult(result);
            NotifyResult(result);
        });
    }

    // The notification for a finished check: actionable when a newer release exists, informational otherwise.
    private void NotifyResult(UpdateCheckResult result)
    {
        switch (result.Availability)
        {
            case UpdateAvailability.Available:
                Notify($"Update available — v{result.AvailableVersion}",
                       $"You’re on v{result.CurrentVersion}. Click to download it and restart Hypertree.",
                       ApplyAction);
                break;
            case UpdateAvailability.UpToDate:
                Notify("You’re up to date", $"Hypertree v{result.CurrentVersion} is the latest release.");
                break;
            case UpdateAvailability.NotApplicable:
                Notify("Update checks need an installed build",
                       "This looks like a dev build. Install Hypertree from a GitHub release and it can update itself.");
                break;
            default:
                Notify("Couldn’t check for updates",
                       "The update feed was unreachable — check your connection and try again.");
                break;
        }
    }

    // The tray's and command palette's "Update now", the Settings install button, and a click on the
    // "update available" notification all land here: clear anything showing, say it's running, then
    // download + install (which restarts the app on success).
    public void ApplyLast()
    {
        if (_last is not { Availability: UpdateAvailability.Available } pending) return;
        _dismissOverlay();
        _settingsWindow()?.OnUpdateApplying();
        Notify($"Downloading v{pending.AvailableVersion}",
               "Hypertree will restart itself once the update is installed.", silent: true);
        _ = ApplyAsync(pending);
    }

    private async Task ApplyAsync(UpdateCheckResult pending)
    {
        try { await UpdateChecker.ApplyAsync(pending).ConfigureAwait(false); }
        catch
        {
            // Download/apply failed (network, locked files). The check handles are now spent, so drop the
            // remembered result — the tray and palette offer a fresh check rather than a stale "Update now".
            OnUi(() =>
            {
                _last = null;
                RefreshMenuItem();
                _settingsWindow()?.OnUpdateFailed();
                Notify("Update failed", "The download or install didn’t complete. Try checking again.");
            });
        }
        // On success the process restarts and never returns here.
    }

    // Retitle the tray's update item to match what's known: "Update now — vX" once a check has found a newer
    // release (so applying it doesn't re-check), else "Check for updates". (Avalonia's NativeMenu has no
    // "about to open" hook, so the item is updated when the state changes instead.)
    private void RefreshMenuItem()
    {
        if (_menuItem is null) return;
        _menuItem.Header = _last is { Availability: UpdateAvailability.Available } ready
            ? $"Update now — v{ready.AvailableVersion}"
            : "Check for updates";
    }

    // Raise a Windows notification. Informational unless given an action, which the user can click.
    private void Notify(string title, string message, string? action = null, bool silent = false)
        => _notifier()?.Show(title, message, action, silent, replaces: NoticeKey);

    // Run on the UI thread. The update continuations resume off a background thread (ConfigureAwait(false)),
    // so every field touch and UI call is marshalled back through here (mirrors App.OnUi).
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
