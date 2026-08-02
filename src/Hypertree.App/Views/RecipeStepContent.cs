using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Recipes;

namespace Hypertree.App.Views;

/// <summary>The edited values of a recipe step, handed back on save (blank optionals folded to null,
/// a blank/invalid monitor to null).</summary>
internal sealed record RecipeStepEdit(string Name, string Target, string? Arguments, string? WorkingDirectory, int? Monitor);

/// <summary>
/// The refine form for one recipe step, hosted as a <b>card</b> on the shared <see cref="OverlayStage"/> —
/// the recipe-side sibling of <see cref="CustomCommandContent"/>, with an extra <b>monitor</b> field and a
/// read-only capture hint (the window's title when it was snapshotted). This is where a captured suggestion
/// becomes a real command: give VS Code its folder as an argument, a terminal its working directory, or
/// move a step to another monitor. Name + target are required; like the prompt it never dismisses on lost
/// focus or a background click, so a half-filled form can't vanish. Esc steps back; Ctrl+Enter saves.
/// </summary>
internal sealed class RecipeStepContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));

    private readonly Action<RecipeStepEdit> _onSave;
    private readonly TextBox _name;
    private readonly TextBox _target;
    private readonly TextBox _args;
    private readonly TextBox _workDir;
    private readonly TextBox _monitor;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    public RecipeStepContent(Action<RecipeStepEdit> onSave, RecipeStep seed)
    {
        _onSave = onSave;

        _name = Field("what this is — e.g. Code", seed.Name);
        _target = Field(@"app, file, folder or URL — e.g. C:\…\Code.exe", seed.Target);
        _args = Field("optional arguments — e.g. a folder for VS Code to open", seed.Arguments);
        _workDir = Field(@"optional working directory — e.g. C:\projects\app", seed.WorkingDirectory);
        _monitor = Field("monitor number (1, 2, …) — blank = wherever it opens", seed.Placement.Monitor?.ToString());

        _ok = new PromptButton("Save");
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Edit step", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "What to launch when this recipe is restored — the capture only knew the app, so add the folder or arguments it should open with.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
        });
        if (!string.IsNullOrWhiteSpace(seed.Hint))
            panel.Children.Add(new TextBlock
            {
                Text = $"captured window: {seed.Hint}", TextWrapping = TextWrapping.Wrap,
                Foreground = Accent, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            });
        panel.Children.Add(Labelled("Name", _name));
        panel.Children.Add(Labelled("Target", _target));
        panel.Children.Add(Labelled("Arguments", _args));
        panel.Children.Add(Labelled("Working directory", _workDir));
        panel.Children.Add(Labelled("Monitor", _monitor));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { _cancel, _ok },
        });

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 460, Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        _root = new Grid { Children = { card } };

        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    private static TextBox Field(string placeholder, string? prefill) =>
        new() { PlaceholderText = placeholder, Text = prefill ?? "" };

    private static Control Labelled(string caption, TextBox field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Muted, FontSize = 11 }, field },
    };

    // ── IStageContent ────────────────────────────────────────────────────────────
    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        _target.Focus();
        _target.SelectAll();
    }

    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { }

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Submit(); e.Handled = true; }
    }

    private void OnBubbleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (!_ok.IsFocused && !_cancel.IsFocused)) return;
        switch (e.Key)
        {
            case Key.Left: _cancel.Focus(); e.Handled = true; break;
            case Key.Right: _ok.Focus(); e.Handled = true; break;
        }
    }

    private void Submit()
    {
        if (_submitted) return;
        string name = _name.Text?.Trim() ?? "";
        string target = _target.Text?.Trim() ?? "";
        if (name.Length == 0 || target.Length == 0) return; // name + target are both required

        _submitted = true;
        _onSave(new RecipeStepEdit(name, target, Optional(_args), Optional(_workDir), Monitor()));
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    private int? Monitor()
    {
        string v = _monitor.Text?.Trim() ?? "";
        return int.TryParse(v, out int m) && m >= 1 ? m : null; // blank / junk / <1 → any
    }

    private static string? Optional(TextBox box)
    {
        string v = box.Text?.Trim() ?? "";
        return v.Length == 0 ? null : v;
    }

    private void Cancel() => _stage?.Back();
}
