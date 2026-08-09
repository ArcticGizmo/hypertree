using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.App.Updates;
using Hypertree.Changelog;
using Hypertree.Platform;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// How the Settings window's Updates row reaches the app's shared update flow. The window never checks
/// or installs itself — one flow means one set of Windows notifications, whichever surface started it.
/// </summary>
/// <param name="Check">Start a check (the app notifies, and calls back into this window).</param>
/// <param name="Install">Download + install the release the last check found; restarts the app.</param>
/// <param name="Last">
/// The most recent check's result, so a window opened afterwards still offers to install it.
/// </param>
internal sealed record UpdateHooks(Action Check, Action Install, Func<UpdateCheckResult?> Last);

/// <summary>
/// The settings window. A normal (focusable, opaque) window rendered in Fluent <b>dark</b> so it
/// matches the board/palette look, summoned from the tray, the map's cog, or the command palette.
/// Because a tray/hotkey process is a background process, it force-foregrounds on open via
/// <see cref="IForegroundActivator"/> so it takes input immediately. There's no Save button — every edit
/// applies and persists at once (see <see cref="ApplyLive"/>); closing the window (or Esc) just dismisses it.
///
/// Startup is the first option (a right-aligned toggle), the desktop label is a placement dropdown (Off,
/// or a corner/edge to dock to), "show the board before moving" is a toggle, and every global hotkey can be
/// rebound: click a chord and press the new combination. The navigation-flash timings are no longer
/// configurable (fixed constants in <c>HudWindow</c>).
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F0B84E"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly IForegroundActivator _activator;
    private readonly Action<AppSettings, bool> _onSave;
    private readonly AppSettings _initial; // carries fields this window doesn't edit (e.g. templates) through Save

    private readonly ToggleSwitch _startOnLogin;
    private readonly ComboBox _taskbarLabelPlacement;
    private readonly ToggleSwitch _showSwitcher;
    private readonly ComboBox _mapStyle;
    private readonly ToggleSwitch _displayBeforeMoving;
    private readonly ToggleSwitch _animateNavigation;
    private readonly ToggleSwitch _sweepFromLeadingEdge;
    private readonly ToggleSwitch _showChangelog;

    // The working set of chords, edited in place by the rebind capture; committed to overrides on Save.
    private readonly Dictionary<HotkeyCommand, HotkeyChord> _chords;
    private readonly Dictionary<HotkeyCommand, Button> _chordButtons = new();
    private readonly TextBlock _hotkeyHint;
    private HotkeyCommand? _capturing; // the command awaiting a new chord, or null when not capturing
    private bool _ready;               // set once the ctor has built everything; gates live-apply against
                                       // any change event that fires while the controls are still wiring up

    // Updates section: a status line, the check button, and an install button revealed only once a newer
    // release has been found. The checking/downloading itself belongs to the app (one flow, one set of
    // Windows notifications, whichever surface started it) — this window only drives it and mirrors it.
    private readonly TextBlock _updateStatus;
    private readonly Button _checkUpdate;
    private readonly Button _installUpdate;
    private readonly UpdateHooks _updates;

    public SettingsWindow(AppSettings settings, bool startOnLogin,
                          Action<AppSettings, bool> onSave, IForegroundActivator activator,
                          UpdateHooks updates)
    {
        _activator = activator;
        _onSave = onSave;
        _updates = updates;
        _initial = settings;
        _chords = new Dictionary<HotkeyCommand, HotkeyChord>(settings.ResolveHotkeys());

        Title = "Hypertree Settings";
        try { Icon = DevChrome.AppWindowIcon(); } catch { }
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Fixed size; the options scroll inside (see below) once they outgrow the height. Sizing to content
        // fought the explicit Width, and — now that there are enough options to run past a shorter screen —
        // would overflow a window that can't be resized. 600 fits comfortably even on a 1080p/150% laptop.
        CanResize = false;
        Width = 720;
        Height = 600;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        _startOnLogin = Toggle(startOnLogin);
        _taskbarLabelPlacement = LabelPlacementSelector(settings.TaskbarLabelPlacement);
        _showSwitcher = Toggle(settings.ShowSwitcher);
        _mapStyle = MapStyleSelector(settings.MapStyle);
        _displayBeforeMoving = Toggle(settings.DisplayBeforeMoving);
        _animateNavigation = Toggle(settings.AnimateNavigation);
        _sweepFromLeadingEdge = Toggle(settings.SweepFromLeadingEdge);
        _showChangelog = Toggle(settings.ShowChangelogOnUpdate);
        _hotkeyHint = new TextBlock
        {
            Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            Text = "Click a shortcut, then press the new combination (needs a modifier). Esc cancels.",
        };

        _updateStatus = new TextBlock
        {
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 2, 0, 0),
            Text = "Check whether a newer release of Hypertree is available. The result arrives as a Windows notification.",
        };
        _checkUpdate = new Button { Content = "Check for updates", HorizontalAlignment = HorizontalAlignment.Right };
        _checkUpdate.Click += (_, _) => _updates.Check();
        _installUpdate = new Button
        {
            Content = "Download & install", HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false,
        };
        _installUpdate.Click += (_, _) => _updates.Install();

        // No Save/Cancel — each control applies (and persists) the moment it changes; see ApplyLive.
        foreach (ToggleSwitch t in new[]
                 { _startOnLogin, _showSwitcher, _displayBeforeMoving,
                   _animateNavigation, _sweepFromLeadingEdge, _showChangelog })
            t.IsCheckedChanged += (_, _) => ApplyLive();
        _mapStyle.SelectionChanged += (_, _) => ApplyLive();
        _taskbarLabelPlacement.SelectionChanged += (_, _) => ApplyLive();

        var options = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                    Title2("Startup"),
                    ToggleRow("Start Hypertree when I log in", _startOnLogin),

                    Divider(),
                    Title2("Desktop label"),
                    SelectRow("Show the current desktop name", _taskbarLabelPlacement),
                    Hint("A small pill naming the desktop you're on, docked to the corner or edge you pick "
                         + "(“Bottom center” sits over the taskbar). It fades out while the cursor is near "
                         + "so what's underneath stays clickable. Choose “Off” to hide it."),

                    Divider(),
                    Title2("Switcher"),
                    ToggleRow("Show the floating branch switcher", _showSwitcher),
                    Hint("A draggable panel (top-right by default) listing every branch in map order with the "
                         + "desktop a click would land on — click a name to jump there, or the desktop chip to "
                         + "pick a different one. Click its header, or press the switcher shortcut, to collapse "
                         + "it to a logo bubble."),

                    Divider(),
                    Title2("Appearance"),
                    SelectRow("Map style", _mapStyle),
                    Hint("How every board is drawn — the flash, the map, previews and the move flow. "
                         + "“Board” is the screen-tile view, “Metro” a transit diagram (coloured lines and "
                         + "stations), “ASCII” a monospace terminal look. Cycle it with “v” on the map."),

                    Divider(),
                    Title2("Navigation"),
                    ToggleRow("Show the board before diving or surfacing", _displayBeforeMoving),
                    Hint("The first dive or surface (up/down between branches) only brings the board up so you "
                         + "can see where you are — keep the modifiers held and press again to move. Moving "
                         + "left/right within a row goes straight away."),
                    ToggleRow("Animate navigation moves", _animateNavigation),
                    Hint("Sweeps a soft gradient across in the direction you moved, echoing the traditional "
                         + "desktop-switch animation. Follows the Windows “Show animations” setting — "
                         + "with that off, no animation plays."),
                    ToggleRow("Sweep from the leading edge", _sweepFromLeadingEdge),
                    Hint("The wipe begins on the side you move toward and sweeps away across the screen. "
                         + "Turn off to have it begin on the opposite side and sweep toward where you're heading."),

                    Divider(),
                    Title2("Hotkeys"),
                    HotkeyRows(),
                    _hotkeyHint,

                    Divider(),
                    Title2("Changelog"),
                    ToggleRow("Show what's new after an update", _showChangelog),
                    ChangelogRow(),

                    Divider(),
                    Title2("Updates"),
                    UpdatesRow(),

                    new TextBlock
                    {
                        Text = "Changes apply and save automatically. Press Esc to close.",
                        Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 14, 0, 2),
                    },
            },
        };

        // The options can outgrow the fixed-height window, so they scroll. There's no pinned button row —
        // changes apply immediately, so there's nothing to confirm.
        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new ScrollViewer
            {
                Content = options,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 8, 0), // keep the scrollbar off the option rows
            },
        };

        // Tunnel so we intercept the chord before anything else swallows it (and Esc-to-close, since there's
        // no Cancel button to carry IsCancel).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // A check run before this window opened still counts: pick its result up so an available update is
        // installable from here without re-checking (matching the tray menu and the palette).
        if (_updates.Last() is { } last) OnUpdateResult(last);
        _ready = true;
    }

    // Build the settings from the current control state — the rebound chords overlaid on the defaults (like
    // ResolveHotkeys), plus the fields this window doesn't edit, carried through untouched.
    private AppSettings CurrentSettings()
    {
        // Persist only the chords that differ from the built-in defaults, so future default changes still
        // reach commands the user never touched (mirrors how ResolveHotkeys overlays them).
        var overrides = new List<HotkeyBinding>();
        foreach (HotkeyCommand cmd in Hotkeys.Order)
        {
            HotkeyChord chord = _chords[cmd];
            if (!chord.Equals(Hotkeys.Defaults[cmd]))
                overrides.Add(new HotkeyBinding(cmd, chord.Modifiers, chord.Key));
        }

        // Only the fields this window actually edits are listed; every other setting — the switcher's
        // position/collapse state, map & picker zoom, the map legend, templates, custom commands, the
        // stamped last-seen version, and anything added to AppSettings in future — rides across untouched
        // via `with`, so toggling one option can never silently reset an unrelated one.
        return _initial with
        {
            TaskbarLabelPlacement = (LabelPlacement)Math.Max(0, _taskbarLabelPlacement.SelectedIndex),
            ShowSwitcher = _showSwitcher.IsChecked ?? false,
            MapStyle = (MapStyle)Math.Max(0, _mapStyle.SelectedIndex),
            DisplayBeforeMoving = _displayBeforeMoving.IsChecked ?? true,
            AnimateNavigation = _animateNavigation.IsChecked ?? true,
            SweepFromLeadingEdge = _sweepFromLeadingEdge.IsChecked ?? true,
            ShowChangelogOnUpdate = _showChangelog.IsChecked ?? true,
            HotkeyBindings = overrides,
        };
    }

    // Apply-and-persist on every change. With no Save button, each toggle or rebind takes effect at once:
    // App's save handler writes settings.json and re-applies everything (taskbar label, map style, startup),
    // so calling it per change keeps the running app in lockstep with the panel. Gated until the ctor is done.
    private void ApplyLive()
    {
        if (!_ready) return;
        _onSave(CurrentSettings(), _startOnLogin.IsChecked ?? false);
    }

    // ── Hotkey rebinding ────────────────────────────────────────────────────────────

    private Control HotkeyRows()
    {
        var rows = new StackPanel { Spacing = 4 };
        foreach (HotkeyCommand cmd in Hotkeys.Order)
        {
            HotkeyCommand c = cmd; // capture per iteration
            var button = new Button
            {
                Content = _chords[c].Display(),
                FontFamily = Mono, FontSize = 12, MinWidth = 170,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) => BeginCapture(c);
            _chordButtons[c] = button;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock
            {
                Text = Hotkeys.DisplayName(c), Foreground = Ink, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(button, 1);
            grid.Children.Add(label);
            grid.Children.Add(button);
            rows.Children.Add(grid);
        }

        var reset = new Button
        {
            Content = "Reset to defaults", FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0),
        };
        reset.Click += (_, _) => ResetHotkeys();
        rows.Children.Add(reset);
        return rows;
    }

    private void BeginCapture(HotkeyCommand cmd)
    {
        // Abandon any capture already in progress (restores its label), then arm the new one.
        if (_capturing is HotkeyCommand prev && prev != cmd) RestoreLabel(prev);
        _capturing = cmd;
        _chordButtons[cmd].Content = "Press keys…";
        SetHint("Press the new combination (needs a modifier). Esc cancels.", Warn);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturing is not HotkeyCommand cmd)
        {
            // No Cancel button to carry IsCancel, so Esc closes the (already-applied) window here.
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            return;
        }
        e.Handled = true; // swallow the chord so it doesn't leak to the window (or close it) mid-capture

        if (e.Key == Key.Escape) { RestoreLabel(cmd); EndCapture(reset: true); return; }
        if (IsModifierKey(e.Key)) return; // wait for the non-modifier that completes the chord

        HotkeyKey? key = MapKey(e.Key);
        if (key is null) { SetHint("Unsupported key — use a letter, digit, arrow or F-key.", Warn); return; }

        HotkeyModifiers mods = MapModifiers(e.KeyModifiers);
        if (mods == HotkeyModifiers.None)
        {
            SetHint("Add a modifier (Ctrl / Alt / Shift / Win) — bare keys aren't allowed.", Warn);
            return;
        }

        var chord = new HotkeyChord(mods, key.Value);
        foreach (KeyValuePair<HotkeyCommand, HotkeyChord> kv in _chords)
        {
            if (kv.Key != cmd && kv.Value == chord)
            {
                SetHint($"{chord.Display()} is already used by “{Hotkeys.DisplayName(kv.Key)}”.", Warn);
                return;
            }
        }

        _chords[cmd] = chord;
        _chordButtons[cmd].Content = chord.Display();
        EndCapture(reset: true);
        ApplyLive(); // rebinding takes effect at once, like every other setting
    }

    private void ResetHotkeys()
    {
        if (_capturing is HotkeyCommand c) RestoreLabel(c);
        _capturing = null;
        foreach (HotkeyCommand cmd in Hotkeys.Order)
        {
            _chords[cmd] = Hotkeys.Defaults[cmd];
            _chordButtons[cmd].Content = _chords[cmd].Display();
        }
        SetHint("Shortcuts reset to defaults.", Muted);
        ApplyLive();
    }

    private void RestoreLabel(HotkeyCommand cmd) => _chordButtons[cmd].Content = _chords[cmd].Display();

    private void EndCapture(bool reset)
    {
        _capturing = null;
        if (reset) SetHint("Click a shortcut, then press the new combination (needs a modifier). Esc cancels.", Muted);
    }

    private void SetHint(string text, IBrush colour)
    {
        _hotkeyHint.Text = text;
        _hotkeyHint.Foreground = colour;
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static HotkeyModifiers MapModifiers(KeyModifiers km)
    {
        HotkeyModifiers m = HotkeyModifiers.None;
        if (km.HasFlag(KeyModifiers.Control)) m |= HotkeyModifiers.Control;
        if (km.HasFlag(KeyModifiers.Alt)) m |= HotkeyModifiers.Alt;
        if (km.HasFlag(KeyModifiers.Shift)) m |= HotkeyModifiers.Shift;
        if (km.HasFlag(KeyModifiers.Meta)) m |= HotkeyModifiers.Win;
        return m;
    }

    // Avalonia Key → the OS-agnostic HotkeyKey. The letter / digit / function-key ranges are contiguous in
    // both enums, so they map by offset; the rest are named explicitly.
    private static HotkeyKey? MapKey(Key k) => k switch
    {
        Key.Up    => HotkeyKey.ArrowUp,
        Key.Down  => HotkeyKey.ArrowDown,
        Key.Left  => HotkeyKey.ArrowLeft,
        Key.Right => HotkeyKey.ArrowRight,
        Key.Space => HotkeyKey.Space,
        >= Key.A  and <= Key.Z   => (HotkeyKey)((int)HotkeyKey.A  + (k - Key.A)),
        >= Key.D0 and <= Key.D9  => (HotkeyKey)((int)HotkeyKey.D0 + (k - Key.D0)),
        >= Key.F1 and <= Key.F12 => (HotkeyKey)((int)HotkeyKey.F1 + (k - Key.F1)),
        _ => null,
    };

    /// <summary>Force to the foreground and take focus — the app calls this right after Show(), since a
    /// global hotkey / tray click in a background process doesn't grant foreground rights on its own.</summary>
    public void TakeFocus()
    {
        if (TryGetPlatformHandle() is { } handle) _activator.ForceForeground(handle.Handle);
        Activate();
        Dispatcher.UIThread.Post(() => (_startOnLogin as Control)?.Focus());
    }

    // ── Changelog ────────────────────────────────────────────────────────────────────

    // A caption and a "View changelog" button that pops the whole file in the same window the post-update
    // "what's new" uses (without the suppress button — this is a deliberate look, not an interruption).
    private Control ChangelogRow()
    {
        var caption = new TextBlock
        {
            Text = "Pop a “what’s new” window listing the releases since the version you were last on.",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 2, 0, 0),
        };
        var view = new Button { Content = "View changelog", HorizontalAlignment = HorizontalAlignment.Right };
        view.Click += (_, _) => OpenFullChangelog();

        return new StackPanel { Spacing = 6, Children = { caption, view } };
    }

    private void OpenFullChangelog()
    {
        string? markdown = ChangelogMarkdown.LoadEmbedded();
        var sections = markdown is null
            ? Array.Empty<ChangelogSection>()
            : ChangelogParser.Parse(markdown);

        var window = new ChangelogWindow("What's new in Hypertree",
            "Everything Hypertree can do, newest first.", sections, _activator)
        {
            Topmost = true, // sit above the settings window that launched it
        };
        window.Show(this);
        window.TakeFocus();
    }

    // ── Updates ────────────────────────────────────────────────────────────────────

    // A status caption plus the check button, with a "Download & install" button that appears only once a
    // newer release has been found. The buttons drive the app's shared update flow (so a check from here
    // raises the same Windows notifications as one from the tray or the palette); the caption below them
    // mirrors it, because a window you deliberately opened is a fine place to read the detail.
    private Control UpdatesRow()
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _installUpdate, _checkUpdate },
        };
        return new StackPanel { Spacing = 6, Children = { _updateStatus, buttons } };
    }

    /// <summary>A check has started — from here or from any other surface.</summary>
    public void OnUpdateCheckStarted()
    {
        _checkUpdate.IsEnabled = false;
        _installUpdate.IsVisible = false;
        SetUpdateStatus("Checking for updates…", Muted);
    }

    /// <summary>A check finished: reveal the install button when there's something to install.</summary>
    public void OnUpdateResult(UpdateCheckResult result)
    {
        _checkUpdate.IsEnabled = true;
        _installUpdate.IsEnabled = true;
        _installUpdate.IsVisible = result.Availability == UpdateAvailability.Available;

        switch (result.Availability)
        {
            case UpdateAvailability.Available:
                SetUpdateStatus($"Update available: v{result.AvailableVersion} (you have v{result.CurrentVersion}).", Accent);
                break;
            case UpdateAvailability.UpToDate:
                SetUpdateStatus($"You’re up to date — v{result.CurrentVersion} is the latest release.", Muted);
                break;
            case UpdateAvailability.NotApplicable:
                SetUpdateStatus("Update checks only run in an installed build — this looks like a dev build. Install Hypertree from a GitHub release and it’ll check automatically.", Muted);
                break;
            default:
                SetUpdateStatus("Couldn’t check for updates — the feed was unreachable. Check your connection and try again.", Warn);
                break;
        }
    }

    /// <summary>The update is downloading; the app restarts itself if it lands.</summary>
    public void OnUpdateApplying()
    {
        _checkUpdate.IsEnabled = false;
        _installUpdate.IsEnabled = false;
        SetUpdateStatus("Downloading and installing… Hypertree will restart.", Accent);
    }

    /// <summary>The download or install didn't complete — the found release is no longer installable.</summary>
    public void OnUpdateFailed()
    {
        _checkUpdate.IsEnabled = true;
        _installUpdate.IsEnabled = true;
        _installUpdate.IsVisible = false;
        SetUpdateStatus("Update failed — the download or install didn’t complete. Try again.", Warn);
    }

    private void SetUpdateStatus(string text, IBrush colour)
    {
        _updateStatus.Text = text;
        _updateStatus.Foreground = colour;
    }

    // ── Layout helpers ───────────────────────────────────────────────────────────────

    private static ToggleSwitch Toggle(bool value) => new()
    {
        IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right,
    };

    // The map-style dropdown. Item order matches the MapStyle enum (Board, Metro, ASCII), so the selected
    // index is the enum value — see SnapshotSettings.
    private static ComboBox MapStyleSelector(MapStyle style) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 132,
        ItemsSource = new[] { "Board", "Metro", "ASCII" },
        SelectedIndex = (int)style,
    };

    // The desktop-label placement dropdown. Item order matches the LabelPlacement enum (Off first, then the
    // corners/edges), so the selected index is the enum value — read back in CurrentSettings.
    private static ComboBox LabelPlacementSelector(LabelPlacement placement) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 132,
        ItemsSource = new[]
        {
            "Off", "Top left", "Top center", "Top right", "Bottom left", "Bottom center", "Bottom right",
        },
        SelectedIndex = (int)placement,
    };

    // A label on the left, its selector pinned right — the ToggleRow shape for a non-toggle control.
    private static Control SelectRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 0, 0, 0) };
        var text = new TextBlock
        {
            Text = label, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    // A label on the left, its toggle pinned right — the shared shape for the on/off options.
    private static Control ToggleRow(string label, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 0, 0, 0) };
        var text = new TextBlock
        {
            Text = label, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        return grid;
    }

    // The explanatory line under a toggle, indented to sit with the option it belongs to.
    private static Control Hint(string text) => new TextBlock
    {
        Text = text, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24, -4, 90, 0),
    };

    private static Control Title2(string text) => new TextBlock
    {
        Text = text, Foreground = Accent, FontWeight = FontWeight.Bold, FontSize = 13,
    };

    private static Control Divider() => new Border
    {
        Height = 1, Background = new SolidColorBrush(Color.Parse("#2A3444")), Margin = new Thickness(0, 8),
    };
}
