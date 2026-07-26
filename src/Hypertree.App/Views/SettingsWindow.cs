using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Changelog;
using Hypertree.Platform;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The settings window. A normal (focusable, opaque) window rendered in Fluent <b>dark</b> so it
/// matches the board/palette look, summoned from the tray, the map's cog, or the command palette.
/// Because a tray/hotkey process is a background process, it force-foregrounds on open via
/// <see cref="IForegroundActivator"/> so it takes input immediately. Edits apply on Save.
///
/// Startup is the first option (a right-aligned toggle), the desktop label is a matching toggle, and every
/// global hotkey can be rebound: click a chord and press the new combination. The navigation-flash timings
/// are no longer configurable (fixed constants in <c>HudWindow</c>).
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
    private readonly ToggleSwitch _showTaskbarLabel;
    private readonly ToggleSwitch _showChangelog;

    // The working set of chords, edited in place by the rebind capture; committed to overrides on Save.
    private readonly Dictionary<HotkeyCommand, HotkeyChord> _chords;
    private readonly Dictionary<HotkeyCommand, Button> _chordButtons = new();
    private readonly TextBlock _hotkeyHint;
    private HotkeyCommand? _capturing; // the command awaiting a new chord, or null when not capturing

    public SettingsWindow(AppSettings settings, bool startOnLogin,
                          Action<AppSettings, bool> onSave, IForegroundActivator activator)
    {
        _activator = activator;
        _onSave = onSave;
        _initial = settings;
        _chords = new Dictionary<HotkeyCommand, HotkeyChord>(settings.ResolveHotkeys());

        Title = "Hypertree Settings";
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://hypertree/Assets/icon.ico"))); } catch { }
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Width = 480;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        _startOnLogin = Toggle(startOnLogin);
        _showTaskbarLabel = Toggle(settings.ShowTaskbarLabel);
        _showChangelog = Toggle(settings.ShowChangelogOnUpdate);
        _hotkeyHint = new TextBlock
        {
            Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            Text = "Click a shortcut, then press the new combination (needs a modifier). Esc cancels.",
        };

        var save = new Button { Content = "Save", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => Commit();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Title2("Startup"),
                    ToggleRow("Start Hypertree when I log in", _startOnLogin),

                    Divider(),
                    Title2("Desktop label"),
                    ToggleRow("Show the current desktop name over the taskbar", _showTaskbarLabel),

                    Divider(),
                    Title2("Hotkeys"),
                    HotkeyRows(),
                    _hotkeyHint,

                    Divider(),
                    Title2("Changelog"),
                    ToggleRow("Show what's new after an update", _showChangelog),
                    ChangelogRow(),

                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                        Children = { cancel, save },
                    },
                },
            },
        };

        // Tunnel so we intercept the chord before any button/default-button handling swallows it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void Commit()
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

        var settings = new AppSettings
        {
            ShowTaskbarLabel = _showTaskbarLabel.IsChecked ?? true,
            ShowChangelogOnUpdate = _showChangelog.IsChecked ?? true,
            LastSeenVersion = _initial.LastSeenVersion, // stamped at startup, not edited here — carry through
            BranchTemplates = _initial.BranchTemplates, // not edited here — carry through untouched
            HotkeyBindings = overrides,
        };
        _onSave(settings, _startOnLogin.IsChecked ?? false);
        Close();
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
        if (_capturing is not HotkeyCommand cmd) return;
        e.Handled = true; // swallow the chord so Save/Cancel defaults don't fire mid-capture

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

    // ── Layout helpers ───────────────────────────────────────────────────────────────

    private static ToggleSwitch Toggle(bool value) => new()
    {
        IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right,
    };

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

    private static Control Title2(string text) => new TextBlock
    {
        Text = text, Foreground = Accent, FontWeight = FontWeight.Bold, FontSize = 13,
    };

    private static Control Divider() => new Border
    {
        Height = 1, Background = new SolidColorBrush(Color.Parse("#2A3444")), Margin = new Thickness(0, 8),
    };
}
