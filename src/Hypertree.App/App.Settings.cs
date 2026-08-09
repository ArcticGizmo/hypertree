using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Ipc;
using Hypertree.App.Status;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Changelog;
using Hypertree.Desktops;
using Hypertree.Ipc;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

public sealed partial class App
{
    // ── Settings (tray · map cog · command palette "settings") ──────────────────────

    private void OpenSettings()
    {
        if (_activator is null || _startup is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }

        // Suspend the global hotkeys while settings is open so the rebind capture reads keystrokes cleanly
        // (an active chord like Ctrl+Alt+P mustn't fire its command while the user is pressing it to rebind).
        // They're re-registered from the (possibly changed) bindings when the window closes.
        SuspendHotkeys();

        _settingsWindow = new SettingsWindow(_settings, _startup.IsEnabled, SaveSettings, _activator,
            new UpdateHooks(CheckForUpdates, ApplyLastUpdate, () => _lastUpdate));
        _settingsWindow.Topmost = true; // sit above the map/flash if one is showing
        // Settings is the one surface that's still its own window; when it closes, re-register the hotkeys
        // (picking up any rebind) and hand the stage its key focus back so an underlying map resumes.
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_shuttingDown) return; // teardown already unregistered; don't resurrect the hotkey threads
            RegisterHotkeys();
            _stage?.Reassert();
            // A map-style change made while settings was open was deferred (see ApplyMapStyle) to avoid a
            // z-order fight; repaint the open map now the window's gone so it picks the new style up.
            RefreshOverlay();
        };
        _settingsWindow.Show();
        _settingsWindow.TakeFocus();
    }

    // Called live on every change in the settings window (there's no Save button). Persists and re-applies
    // each time; hotkey re-registration is still left to the window's Closed handler, so the global hotkeys
    // stay suspended for exactly as long as the window is open (and a rebind lands cleanly on close).
    private void SaveSettings(AppSettings settings, bool startOnLogin)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
        ApplyTaskbarLabel();
        ApplySwitcher();
        ApplyMapStyle();
        _startup?.SetEnabled(startOnLogin);
    }

    // v on the map (or the Settings selector) cycles the board style board → metro → ascii → board. It's a
    // persisted, app-wide choice, so we update the setting, save it, and push it onto the stage — every
    // surface that draws a board follows.
    private void ToggleMapStyle()
    {
        _settings.MapStyle = _settings.MapStyle switch
        {
            MapStyle.Board => MapStyle.Metro,
            MapStyle.Metro => MapStyle.Ascii,
            _ => MapStyle.Board,
        };
        _settingsStore?.Save(_settings);
        ApplyMapStyle();
    }

    // Push the current style onto the stage and repaint whatever it's showing, so the switch is immediate
    // (the interactive map re-renders; a card's backdrop refreshes behind it). A no-op when the style hasn't
    // actually changed, so live-apply toggling an unrelated setting doesn't churn the board.
    private void ApplyMapStyle()
    {
        if (_stage is null || _stage.MapStyle == _settings.MapStyle) return;
        _stage.MapStyle = _settings.MapStyle;
        // The spatial map's metrics are style-independent (only the room glyph changes), so leave its camera
        // put when it's open; only reframe when navigating with the map closed (the flash uses the offset).
        if (_spatialOverlay is not { IsOpen: true }) _mapCamera.Reframe();
        // While the Settings window is open it sits above the stage; re-rendering the map here would end in
        // _stage.BringToFront() and steal the top of the z-order from it. Defer the map repaint to the
        // Settings Closed handler; refreshing a card backdrop (no z-order change) is safe either way.
        if (_settingsWindow is null && _model is not null && _spatialOverlay is { IsOpen: true })
            _spatialOverlay.SetSource(_model.BuildSpatialSource(), _spatial);
        _stage.RefreshBackdrop();
    }

    // Position (or hide) the persistent taskbar label to match the placement setting.
    private void ApplyTaskbarLabel()
    {
        if (_taskbarLabel is null) return;
        _taskbarLabel.SetPlacement(_settings.TaskbarLabelPlacement);
    }

    // Show or hide the floating branch switcher to match the setting.
    private void ApplySwitcher()
    {
        if (_switcher is null) return;
        if (_settings.ShowSwitcher) _switcher.Enable();
        else _switcher.Disable();
    }

    // Ctrl+Alt+W — collapse the switcher to its bubble, or expand it. A no-op when the switcher is off:
    // the chord is registered regardless (like every command), but there's nothing to toggle.
    private void ToggleSwitcherCollapsed()
    {
        if (_settings.ShowSwitcher) _switcher?.ToggleCollapsed();
    }

    // A jump from the switcher: switch to the row (a branch by id, or main when null), landing on the
    // chosen desktop or — when null — the row's resume point. Reconcile first so a desktop deleted from
    // Task View since the last snapshot never traps the click (mirrors the map / CLI goto path).
    private void JumpFromSwitcher(Guid? branchId, int? desktop)
    {
        if (_model is null || _desktops is null) return;
        _model.Reconcile();
        DesktopId from = _desktops.Current;
        if (_model.GoTo(branchId, desktop, out _) != GoToResult.Ok) return;
        RecordVisit(from);
        // If the map happens to be open (it suppresses the switcher, but be safe), keep it in step.
        SyncOpenMapToCurrent();
    }

    // The switcher persists its own position (after a drag) and collapse state through these, folded into
    // the same settings file everything else uses. The expanded panel and the collapsed bubble keep separate
    // coordinates, so dragging one never moves the other.
    private void SaveSwitcherPosition(bool collapsed, Avalonia.PixelPoint at)
    {
        if (collapsed) { _settings.SwitcherCollapsedX = at.X; _settings.SwitcherCollapsedY = at.Y; }
        else { _settings.SwitcherX = at.X; _settings.SwitcherY = at.Y; }
        _settingsStore?.Save(_settings);
    }

    private void SaveSwitcherCollapsed(bool collapsed)
    {
        _settings.SwitcherCollapsed = collapsed;
        _settingsStore?.Save(_settings);
    }

    // Right-click → "Exit Hypertree": a direct shutdown, like the tray's Exit item (no overlay confirm — the
    // menu choice is already deliberate).
    private void ExitFromSwitcher()
        => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // The (branch, name) the taskbar label should show for the desktop the OS is currently on — resolved
    // by id, so it's right even after a switch made outside Hypertree. Null before startup / during teardown.
    private (string? branch, string name)? CurrentDesktopLabel()
    {
        if (_model is null || _desktops is null) return null;
        (string? branch, string label) = _model.Describe(_desktops.Current);
        return (branch, label);
    }
}
