using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Changelog;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>
/// The "what's new" card: a headline, a scrollable list of changelog sections, and a Close button — plus,
/// when a <c>onSuppress</c> callback is supplied (the post-update pop-up), a "Don't show changelogs again"
/// button that flips <c>ShowChangelogOnUpdate</c> off. Shown once per version bump from the startup check,
/// and reused (without the suppress button) by the Settings "View changelog" button to show the whole file.
/// Because a tray process isn't the foreground app, it force-foregrounds on open via
/// <see cref="IForegroundActivator"/>, mirroring <see cref="SettingsWindow"/>.
/// </summary>
internal sealed class ChangelogWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));

    private readonly IForegroundActivator _activator;
    private readonly Action? _onSuppress;

    public ChangelogWindow(string headline, string subhead, IReadOnlyList<ChangelogSection> sections,
                           IForegroundActivator activator, Action? onSuppress = null)
    {
        _activator = activator;
        _onSuppress = onSuppress;

        Title = "Hypertree — What's new";
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://hypertree/Assets/icon.ico"))); } catch { }
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        // Wide enough that a changelog bullet reads as one line rather than wrapping two or three times.
        // The window can't be resized, so the width has to be right here — there's no dragging it out.
        Width = 720;
        Height = 600;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        Content = BuildCard(headline, subhead, sections);
    }

    private Control BuildCard(string headline, string subhead, IReadOnlyList<ChangelogSection> sections)
    {
        var title = new TextBlock { Text = headline, Foreground = Ink, FontWeight = FontWeight.Bold, FontSize = 16 };
        var sub = new TextBlock
        {
            Text = subhead, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var header = new StackPanel { Children = { title, sub } };

        var body = new StackPanel();
        if (sections.Count == 0)
            body.Children.Add(new TextBlock { Text = "No changelog entries to show.", Foreground = Muted, FontSize = 12 });
        for (int i = 0; i < sections.Count; i++)
            ChangelogMarkdown.Render(body, sections[i].Block);

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (_onSuppress is not null)
        {
            var suppress = new Button { Content = "Don't show changelogs again" };
            suppress.Click += (_, _) => { try { _onSuppress(); } catch { } Close(); };
            buttons.Children.Add(suppress);
        }
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 84 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22) };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>Force to the foreground and take focus — the app calls this right after Show(), since a
    /// tray/hotkey process doesn't get foreground rights on its own (mirrors <see cref="SettingsWindow"/>).</summary>
    public void TakeFocus()
    {
        if (TryGetPlatformHandle() is { } handle) _activator.ForceForeground(handle.Handle);
        Activate();
        Dispatcher.UIThread.Post(() => Focus());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
