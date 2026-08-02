using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>The entered values for one command in the loadout builder: an optional display name, the command
/// line to run (parsed into target + args at save), and an optional working directory.</summary>
internal sealed record CommandFormResult(string Name, string CommandLine, string? WorkingDirectory);

/// <summary>
/// Add/edit form for a single command in a monitor's list (loadout builder), hosted as a <b>card</b> on the
/// <see cref="OverlayStage"/>. One command line typed naturally — <c>code C:\proj</c>, <c>wt -d "C:\a b"</c>
/// — plus an optional name and working directory. The command is required; like the other forms it never
/// dismisses on lost focus. Esc steps back, Ctrl+Enter saves.
/// </summary>
internal sealed class CommandFormContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly Action<CommandFormResult> _onSave;
    private readonly TextBox _command;
    private readonly TextBox _name;
    private readonly TextBox _workDir;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    public CommandFormContent(Action<CommandFormResult> onSave, string title,
                              string? name = null, string? commandLine = null, string? workingDirectory = null)
    {
        _onSave = onSave;

        _command = Field(@"e.g. code C:\proj   ·   wt -d ""C:\proj""", commandLine);
        _name = Field("optional label — e.g. Editor", name);
        _workDir = Field(@"optional working directory — e.g. C:\projects\app", workingDirectory);

        _ok = new PromptButton("Save");
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "The command run on this monitor when the loadout is applied — launched through the shell, like Win+R.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(Labelled("Command", _command));
        panel.Children.Add(Labelled("Name", _name));
        panel.Children.Add(Labelled("Working directory", _workDir));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { _cancel, _ok },
        });

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 480, Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        _root = new Grid { Children = { card } };
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
    }

    private static TextBox Field(string placeholder, string? prefill) =>
        new() { PlaceholderText = placeholder, Text = prefill ?? "" };

    private static Control Labelled(string caption, TextBox field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Muted, FontSize = 11 }, field },
    };

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        _command.Focus();
        _command.SelectAll();
    }

    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { }

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Submit(); e.Handled = true; }
    }

    private void Submit()
    {
        if (_submitted) return;
        string command = _command.Text?.Trim() ?? "";
        if (command.Length == 0) return; // the command is required

        _submitted = true;
        _onSave(new CommandFormResult(_name.Text?.Trim() ?? "", command, Optional(_workDir)));
        if (_stage?.Current == this) _stage.Back(); // return to the builder, which rebuilds itself
    }

    private static string? Optional(TextBox box)
    {
        string v = box.Text?.Trim() ?? "";
        return v.Length == 0 ? null : v;
    }

    private void Cancel() => _stage?.Back();
}
