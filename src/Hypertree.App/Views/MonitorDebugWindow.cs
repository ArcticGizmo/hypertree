using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>One window's line in the debug overlay: its title, the monitor it's currently on, and the
/// monitor the saved layout wants it on (null when there's no saved layout to compare against).</summary>
internal sealed record DebugWindowRow(string Title, string CurrentMonitor, string? WantedMonitor)
{
    public bool Drifted => WantedMonitor is not null
        && !string.Equals(CurrentMonitor, WantedMonitor, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The windows currently on one monitor, within one desktop.</summary>
internal sealed record DebugMonitorBox(string MonitorLabel, IReadOnlyList<DebugWindowRow> Windows);

/// <summary>One virtual desktop's row: a label and a box per monitor.</summary>
internal sealed record DebugDesktopRow(string DesktopLabel, IReadOnlyList<DebugMonitorBox> Monitors);

/// <summary>
/// A deliberately-rough debug overlay for monitor-layout restore: a scrollable list of virtual desktops,
/// each row holding a box per monitor that lists the windows on it, with each window showing the monitor
/// it's currently on and the one the saved layout wants it on (drifted windows highlighted). A "Restore"
/// button applies the saved layout when there is one. Not polished — it exists to make "which windows do we
/// think are on the wrong monitor" visible at a glance. Force-foregrounds on open like the other tray
/// windows (<see cref="ChangelogWindow"/>).
/// </summary>
internal sealed class MonitorDebugWindow : Window
{
    private static readonly IBrush Ink = Palette.InkBrush;
    private static readonly IBrush Muted = Palette.MutedBrush;
    private static readonly IBrush Drift = new SolidColorBrush(Color.Parse("#E8A13C")); // amber = wrong monitor
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#6FBF7F"));    // green = where it belongs
    private static readonly IBrush BoxBg = new SolidColorBrush(Color.Parse("#181D28"));
    private static readonly IBrush BoxStroke = Palette.StrokeBrush;
    private static readonly IBrush RowBg = new SolidColorBrush(Color.Parse("#0E121A"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly IForegroundActivator _activator;
    private readonly Func<(IReadOnlyList<DebugDesktopRow> desktops, string subtitle)> _provider;
    private readonly Action? _onRestore;
    private readonly Action? _onTrace;
    private readonly TextBlock _subtitle;
    private readonly ScrollViewer _scroller;

    /// <param name="provider">Rebuilds the view data from the live state — called on open, on Refresh, and
    /// after a Restore, so the overlay reflects where windows actually landed rather than a stale snapshot.</param>
    /// <param name="onRestore">Applies the saved layout, or null when there's nothing saved for this set.
    /// Restore does <em>not</em> close the window — it re-polls so you can watch the result.</param>
    /// <param name="onTrace">Runs a diagnostic traced restore and writes a report to disk (debug), or null.</param>
    public MonitorDebugWindow(Func<(IReadOnlyList<DebugDesktopRow>, string)> provider,
                              IForegroundActivator activator, Action? onRestore, Action? onTrace)
    {
        _activator = activator;
        _provider = provider;
        _onRestore = onRestore;
        _onTrace = onTrace;

        Title = "Hypertree — Monitor placement (debug)";
        try { Icon = DevChrome.AppWindowIcon(); } catch { }
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;
        Width = 1040;
        Height = 720;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        _subtitle = new TextBlock
        {
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12),
        };

        Content = BuildShell();
        RefreshView();
    }

    private Control BuildShell()
    {
        var title = new TextBlock { Text = "Monitor placement", Foreground = Ink, FontWeight = FontWeight.Bold, FontSize = 16 };
        var legend = new TextBlock
        {
            Text = "amber = on the wrong monitor · green = where the saved layout wants it",
            Foreground = Muted, FontSize = 11, FontFamily = Mono, Margin = new Thickness(0, 4, 0, 0),
        };
        var header = new StackPanel { Children = { title, _subtitle, legend } };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (_onRestore is not null)
        {
            var restore = new Button { Content = "Restore saved layout", MinWidth = 84 };
            // Run the restore, then re-poll so the new state shows — after a short beat, since the shell can
            // take a moment to settle the moved windows. The window stays open for the next iteration.
            restore.Click += (_, _) =>
            {
                try { _onRestore(); } catch { }
                RefreshView();                        // immediate, then again once things settle
                DispatcherTimer.RunOnce(RefreshView, TimeSpan.FromMilliseconds(350));
            };
            buttons.Children.Add(restore);
        }
        if (_onTrace is not null)
        {
            var trace = new Button { Content = "Trace restore → file", MinWidth = 84 };
            trace.Click += (_, _) => { try { _onTrace(); } catch { } };
            buttons.Children.Add(trace);
        }
        var refresh = new Button { Content = "Refresh", MinWidth = 84 };
        refresh.Click += (_, _) => RefreshView();
        buttons.Children.Add(refresh);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 84 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22) };
        Grid.SetRow(header, 0);
        Grid.SetRow(_scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(_scroller);
        grid.Children.Add(buttons);
        return grid;
    }

    // Re-pull the live view and rebuild the scroll body in place, preserving the scroll offset so a restore
    // you're watching doesn't jump you back to the top.
    private void RefreshView()
    {
        Vector offset = _scroller.Offset;
        (IReadOnlyList<DebugDesktopRow> desktops, string subtitle) = _provider();
        _subtitle.Text = subtitle;

        var body = new StackPanel { Spacing = 10 };
        if (desktops.Count == 0)
            body.Children.Add(new TextBlock { Text = "No desktops to show.", Foreground = Muted, FontSize = 12 });
        foreach (DebugDesktopRow d in desktops) body.Children.Add(BuildDesktopRow(d));
        _scroller.Content = body;
        Dispatcher.UIThread.Post(() => _scroller.Offset = offset, DispatcherPriority.Background);
    }

    private Control BuildDesktopRow(DebugDesktopRow d)
    {
        var label = new TextBlock
        {
            Text = d.DesktopLabel, Foreground = Ink, FontWeight = FontWeight.SemiBold, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6),
        };

        // Monitor boxes wrap to the next line rather than forcing horizontal scroll on many-monitor rigs.
        var boxes = new WrapPanel { Orientation = Orientation.Horizontal };
        if (d.Monitors.Count == 0)
            boxes.Children.Add(new TextBlock { Text = "(no windows)", Foreground = Muted, FontSize = 12 });
        foreach (DebugMonitorBox m in d.Monitors) boxes.Children.Add(BuildMonitorBox(m));

        return new Border
        {
            Background = RowBg, CornerRadius = new CornerRadius(8), BorderBrush = BoxStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(12),
            Child = new StackPanel { Children = { label, boxes } },
        };
    }

    private Control BuildMonitorBox(DebugMonitorBox m)
    {
        var head = new TextBlock
        {
            Text = m.MonitorLabel, Foreground = Ink, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var list = new StackPanel { Spacing = 4 };
        if (m.Windows.Count == 0)
            list.Children.Add(new TextBlock { Text = "(empty)", Foreground = Muted, FontSize = 11, FontFamily = Mono });
        foreach (DebugWindowRow w in m.Windows) list.Children.Add(BuildWindowRow(w));

        return new Border
        {
            Background = BoxBg, CornerRadius = new CornerRadius(6), BorderBrush = BoxStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 8, 8),
            Width = 300, Child = new StackPanel { Children = { head, list } },
        };
    }

    private Control BuildWindowRow(DebugWindowRow w)
    {
        var title = new TextBlock
        {
            Text = w.Title.Length == 0 ? "(untitled)" : w.Title, Foreground = Ink, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        string detail = w.WantedMonitor is null
            ? $"on {w.CurrentMonitor}"
            : w.Drifted ? $"on {w.CurrentMonitor}  →  wants {w.WantedMonitor}"
                        : $"on {w.CurrentMonitor}  ✓";
        var sub = new TextBlock
        {
            Text = detail, FontSize = 11, FontFamily = Mono,
            Foreground = w.WantedMonitor is null ? Muted : w.Drifted ? Drift : Ok,
        };

        return new Border
        {
            Padding = new Thickness(0, 2), Child = new StackPanel { Children = { title, sub } },
        };
    }

    /// <summary>Force to the foreground after Show() — a tray process has no foreground rights of its own.</summary>
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
