using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Loadouts;

namespace Hypertree.App.Views;

/// <summary>
/// Fills a loadout's <c>{name}</c> variables before it's applied (docs/design/session-restore.md): one field
/// per variable, prefilled with its declared default, so the same loadout can outfit any project. Hosted as a
/// card on the <see cref="OverlayStage"/>. A folder variable is flagged so you know a path is wanted (a
/// picker comes later); the built-in <c>{dir}</c> notes that the <c>htree</c> CLI fills it from the current
/// directory. Every field is required — a blank would launch a broken command. Esc cancels; Ctrl+Enter runs.
/// </summary>
internal sealed class VariableFillContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));

    private readonly Action<IReadOnlyDictionary<string, string>> _onFill;
    private readonly List<(string Name, TextBox Box)> _fields = new();
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    public VariableFillContent(IReadOnlyList<VariableSpec> specs, string loadoutName,
                               Action<IReadOnlyDictionary<string, string>> onFill)
    {
        _onFill = onFill;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = $"Apply “{loadoutName}”", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Fill in the loadout's variables — its commands use these before it builds the branch.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (VariableSpec spec in specs)
        {
            var box = new TextBox { Text = spec.Default ?? "", PlaceholderText = Placeholder(spec) };
            _fields.Add((spec.Name, box));
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Label(spec), Foreground = spec.IsDir ? Accent : Muted, FontSize = 11 },
                    box,
                },
            });
        }

        _ok = new PromptButton("Apply");
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;
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
    }

    private static string Label(VariableSpec s) =>
        s.IsDir ? $"{s.Name}   —   the htree CLI fills this from the current directory"
        : s.Kind == VariableKind.Folder ? $"{s.Name}   (folder)"
        : s.Name;

    private static string Placeholder(VariableSpec s) =>
        s.Kind == VariableKind.Folder ? @"a folder — e.g. C:\repos\app" : $"value for {{{s.Name}}}";

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        if (_fields.Count > 0) { _fields[0].Box.Focus(); _fields[0].Box.SelectAll(); }
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

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, TextBox box) in _fields)
        {
            string v = box.Text?.Trim() ?? "";
            if (v.Length == 0) { box.Focus(); return; } // every variable is required
            values[name] = v;
        }

        _submitted = true;
        _onFill(values);
        if (_stage?.Current == this) _stage.Back();
    }

    private void Cancel() => _stage?.Back();
}
