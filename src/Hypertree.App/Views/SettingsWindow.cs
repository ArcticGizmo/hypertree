using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Platform;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The settings window. A normal (focusable, opaque) window rendered in Fluent <b>dark</b> so it
/// matches the board/palette look, summoned from the tray, the map's cog, or the command palette.
/// Because a tray/hotkey process is a background process, it force-foregrounds on open via
/// <see cref="IForegroundActivator"/> so it takes input immediately. Edits apply on Save.
///
/// This first pass wires the real toggles — the navigation-flash behaviour and start-on-login — and
/// shows the hotkey chords read-only (rebinding is a later, M3 job).
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly IForegroundActivator _activator;
    private readonly Action<AppSettings, bool> _onSave;

    private readonly CheckBox _holdToKeep;
    private readonly NumericUpDown _grace;
    private readonly NumericUpDown _timeout;
    private readonly CheckBox _startOnLogin;

    public SettingsWindow(AppSettings settings, bool startOnLogin,
                          Action<AppSettings, bool> onSave, IForegroundActivator activator)
    {
        _activator = activator;
        _onSave = onSave;

        Title = "Hypertree Settings";
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Width = 460;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        _holdToKeep = new CheckBox
        {
            Content = "Keep the navigation flash open while Ctrl+Alt is held",
            IsChecked = settings.FlashHoldToKeep, Foreground = Ink,
        };
        _grace = Number(settings.FlashGraceMs, 0, 2000, 50);
        _timeout = Number(settings.FlashTimeoutMs, 200, 10000, 100);
        _startOnLogin = new CheckBox
        {
            Content = "Start Hypertree when I log in", IsChecked = startOnLogin, Foreground = Ink,
        };

        // The two timings are mutually exclusive: grace applies in hold-to-keep mode, the fixed
        // timeout otherwise. Enable whichever the checkbox selects.
        void SyncEnabled()
        {
            bool hold = _holdToKeep.IsChecked ?? true;
            _grace.IsEnabled = hold;
            _timeout.IsEnabled = !hold;
        }
        _holdToKeep.IsCheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();

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
                    Title2("Navigation flash"),
                    _holdToKeep,
                    Field("Hide delay after release (ms)", _grace),
                    Field("Auto-hide after (ms)", _timeout),

                    Divider(),
                    Title2("Startup"),
                    _startOnLogin,

                    Divider(),
                    Title2("Hotkeys"),
                    HotkeysStub(),

                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                        Children = { cancel, save },
                    },
                },
            },
        };
    }

    private void Commit()
    {
        var settings = new AppSettings
        {
            FlashHoldToKeep = _holdToKeep.IsChecked ?? true,
            FlashGraceMs = (int)(_grace.Value ?? 100),
            FlashTimeoutMs = (int)(_timeout.Value ?? 1500),
        };
        _onSave(settings, _startOnLogin.IsChecked ?? false);
        Close();
    }

    /// <summary>Force to the foreground and take focus — the app calls this right after Show(), since a
    /// global hotkey / tray click in a background process doesn't grant foreground rights on its own.</summary>
    public void TakeFocus()
    {
        if (TryGetPlatformHandle() is { } handle) _activator.ForceForeground(handle.Handle);
        Activate();
        Dispatcher.UIThread.Post(() => (_holdToKeep as Control)?.Focus());
    }

    private static NumericUpDown Number(int value, int min, int max, int step) => new()
    {
        Value = value, Minimum = min, Maximum = max, Increment = step,
        FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Control Field(string label, Control input) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(24, 0, 0, 0),
        Children =
        {
            new TextBlock { Text = label, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 210 },
            input,
        },
    };

    private static Control Title2(string text) => new TextBlock
    {
        Text = text, Foreground = Accent, FontWeight = FontWeight.Bold, FontSize = 13,
    };

    private static Control Divider() => new Border
    {
        Height = 1, Background = new SolidColorBrush(Color.Parse("#2A3444")), Margin = new Thickness(0, 8),
    };

    private static Control HotkeysStub()
    {
        var rows = new StackPanel { Spacing = 3, Margin = new Thickness(24, 0, 0, 0) };
        void Row(string chord, string what) => rows.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new TextBlock { Text = chord, Foreground = Ink, FontFamily = Mono, FontSize = 12, Width = 170 },
                new TextBlock { Text = what, Foreground = Muted, FontSize = 12 },
            },
        });
        Row("Ctrl+Alt+↑ ↓ ← →", "navigate");
        Row("Ctrl+Alt+P", "command palette");
        rows.Children.Add(new TextBlock
        {
            Text = "Rebinding coming soon.", Foreground = Muted, FontSize = 11,
            FontStyle = FontStyle.Italic, Margin = new Thickness(0, 4, 0, 0),
        });
        return rows;
    }
}
