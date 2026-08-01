using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Recipes;

namespace Hypertree.App.Views;

/// <summary>The choice made when a restore finishes with windows still sitting on the staging desktop.</summary>
internal enum RestoreDecision
{
    /// <summary>Finish, leaving any unplaced windows where they are (on staging, kept in the branch).</summary>
    Finish,

    /// <summary>Close the windows we launched that never got placed, then finish.</summary>
    CleanUp,
}

/// <summary>
/// The blocking overlay a recipe restore runs behind (docs/design/session-restore.md): a full-surface card
/// listing every step grouped by its target desktop, each row live-updating through its
/// <see cref="StepState"/> as the executor launches it, finds its window, and places it. A Cancel button
/// (and Esc) stops launching further steps; when the run ends with windows left on staging, the footer
/// turns into a clean-up / leave choice.
///
/// The App owns the run loop and mutates the <see cref="RunStep"/>s directly, then calls
/// <see cref="Refresh"/> on the UI thread; this view only renders their current state and reports the
/// user's cancel/finish choices back.
/// </summary>
internal sealed class RestoreProgressContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#5BD68A"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#E8B75B"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E86A6A"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly string _title;
    private readonly IReadOnlyList<RunStep> _steps;

    private readonly TextBlock _status;
    private readonly StackPanel _list;
    private readonly StackPanel _footer;
    private readonly Control _root;

    private bool _running = true;
    private TaskCompletionSource<RestoreDecision>? _finish;

    /// <summary>Raised (once) when the user asks to stop while the run is still going.</summary>
    public event Action? Cancelled;

    public RestoreProgressContent(string title, IReadOnlyList<RunStep> steps)
    {
        _title = title;
        _steps = steps;

        _status = new TextBlock { Foreground = Muted, FontFamily = Mono, FontSize = 13, Margin = new Thickness(0, 4, 0, 12) };
        _list = new StackPanel { Spacing = 2 };
        _footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };

        var card = new Border
        {
            Background = CardBg,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 20),
            MaxWidth = 760,
            MaxHeight = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = _title, Foreground = Ink, FontFamily = Mono, FontSize = 18, FontWeight = FontWeight.SemiBold },
                    _status,
                    new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, MaxHeight = 440 },
                    _footer,
                },
            },
        };

        _root = new Panel { Children = { card } };
        Refresh();
        ShowCancel();
    }

    // ── IStageContent ────────────────────────────────────────────────────────────
    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface;   // we draw our own card over the stage's dim
    public bool DismissOnDeactivate => false;            // a desktop switch mid-run must not close us
    public bool DismissOnClickAway => false;
    public void OnPresented(OverlayStage stage) { }
    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_running) RequestCancel();
            else _finish?.TrySetResult(RestoreDecision.Finish); // deciding: Esc = leave & finish
            e.Handled = true;
        }
    }

    // ── Driven by the App's run loop ──────────────────────────────────────────────

    /// <summary>Re-render every row from the steps' current state, and the running-count summary. Call on
    /// the UI thread after each transition.</summary>
    public void Refresh()
    {
        _list.Children.Clear();
        string? group = null;
        foreach (RunStep s in _steps)
        {
            if (s.DesktopLabel != group)
            {
                group = s.DesktopLabel;
                _list.Children.Add(new TextBlock
                {
                    Text = group, Foreground = Accent, FontFamily = Mono, FontSize = 13,
                    FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4),
                });
            }
            _list.Children.Add(Row(s));
        }

        int done = _steps.Count(s => s.State == StepState.Done);
        int issues = _steps.Count(s => s.State is StepState.Error or StepState.AlreadyOpen);
        _status.Text = _running
            ? $"placing {done}/{_steps.Count}…" + (issues > 0 ? $"  ·  {issues} to review" : "")
            : $"{done} placed" + (issues > 0 ? $"  ·  {issues} needed review" : "");
    }

    private Control Row(RunStep s)
    {
        (string glyph, IBrush colour, string label) = s.State switch
        {
            StepState.NotStarted  => ("·", Muted, "queued"),
            StepState.Creating    => ("◌", Accent, "launching…"),
            StepState.Placing     => ("→", Accent, "placing…"),
            StepState.Done        => ("✓", Green, "placed"),
            StepState.AlreadyOpen => ("≡", Amber, s.Note ?? "already open"),
            StepState.Error       => ("✕", Red, s.Note ?? "failed"),
            _                     => ("·", Muted, ""),
        };

        var grid = new Grid { Margin = new Thickness(6, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var badge = new TextBlock { Text = glyph, Foreground = colour, FontFamily = Mono, FontSize = 14, Width = 20, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = s.Step.Name, Foreground = Ink, FontFamily = Mono, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { Text = label, Foreground = colour, FontFamily = Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(badge, 0); Grid.SetColumn(name, 1); Grid.SetColumn(state, 2);
        grid.Children.Add(badge); grid.Children.Add(name); grid.Children.Add(state);
        return grid;
    }

    /// <summary>Reflect a cancel request in the UI (called by the App once it observes the flag too).</summary>
    public void MarkCancelling() => _status.Text = "stopping — finishing the step in flight…";

    /// <summary>
    /// End the run: if <paramref name="residueOnStaging"/> windows are still on staging, offer clean-up vs
    /// leave; otherwise a plain Done. Returns the user's choice. Switches the view out of "running" mode.
    /// </summary>
    public Task<RestoreDecision> Finish(int residueOnStaging)
    {
        _running = false;
        Refresh();
        _finish = new TaskCompletionSource<RestoreDecision>();
        _footer.Children.Clear();

        if (residueOnStaging > 0)
        {
            _status.Text = $"{residueOnStaging} app{(residueOnStaging == 1 ? "" : "s")} launched but not placed (still on a staging desktop).";
            _footer.Children.Add(Btn($"Close the {residueOnStaging} & finish", () => _finish.TrySetResult(RestoreDecision.CleanUp), danger: true));
            _footer.Children.Add(Btn("Leave them & finish", () => _finish.TrySetResult(RestoreDecision.Finish)));
        }
        else
        {
            _footer.Children.Add(Btn("Done", () => _finish.TrySetResult(RestoreDecision.Finish), accent: true));
        }
        return _finish.Task;
    }

    private void ShowCancel()
    {
        _footer.Children.Clear();
        _footer.Children.Add(Btn("Cancel", RequestCancel));
    }

    private void RequestCancel()
    {
        if (!_running) return;
        MarkCancelling();
        foreach (Control c in _footer.Children) if (c is Button b) b.IsEnabled = false;
        Cancelled?.Invoke();
    }

    private Button Btn(string text, Action onClick, bool accent = false, bool danger = false)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(16, 8),
            FontFamily = Mono,
            FontSize = 13,
            Foreground = danger ? Red : accent ? Accent : Ink,
            Background = new SolidColorBrush(Color.Parse("#1B2230")),
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
