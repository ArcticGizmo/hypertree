using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The add/edit form for a launcher <see cref="CustomCommand"/>, hosted as a <b>card</b> on the shared
/// <see cref="OverlayStage"/> — the multi-field sibling of <see cref="PromptContent"/>. Four fields: a
/// display name and a shell target (both required), plus optional arguments and a working directory. On
/// save it hands back a built <see cref="CustomCommand"/> (blank optionals folded to null) and unwinds via
/// <see cref="OverlayStage.CompleteToBase"/>; like the prompt, it never dismisses on lost focus or a
/// background click, so a half-filled form can't vanish. Esc steps back.
/// </summary>
internal sealed class CustomCommandContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly Action<CustomCommand> _onSave;
    private readonly TextBox _name;
    private readonly TextBox _target;
    private readonly TextBox _args;
    private readonly TextBox _workDir;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    /// <param name="seed">Prefills the fields — the command being edited, or one carrying just a typed-in
    /// name for a fresh add. Null starts blank.</param>
    /// <param name="isEdit">Titles the card "Edit"/"Add" and the button "Save"/"Add". Kept separate from
    /// <paramref name="seed"/> so a name-prefilled add still reads as an add.</param>
    public CustomCommandContent(Action<CustomCommand> onSave, CustomCommand? seed = null, bool isEdit = false)
    {
        _onSave = onSave;
        CustomCommand? existing = seed;

        _name = Field("e.g. Open work email", existing?.Name);
        _target = Field(@"app, file, folder or URL — e.g. https://mail.google.com", existing?.Target);
        _args = Field("optional arguments", existing?.Arguments);
        _workDir = Field(@"optional working directory — e.g. C:\projects", existing?.WorkingDirectory);

        _ok = new PromptButton(isEdit ? "Save" : "Add");
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 440, Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = isEdit ? "Edit custom command" : "Add custom command",
                                    FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Launched through the shell, exactly as if you double-clicked it.",
                                    TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12,
                                    Margin = new Thickness(0, 0, 0, 4) },
                    Labelled("Name", _name),
                    Labelled("Target", _target),
                    Labelled("Arguments", _args),
                    Labelled("Working directory", _workDir),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
                        Children = { _cancel, _ok },
                    },
                },
            },
        };
        _root = new Grid { Children = { card } };

        // Tunnel Esc so it wins before the text boxes. Ctrl+Enter submits from any field (a bare Enter just
        // moves within a field / activates a focused button, matching the prompt).
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    private static TextBox Field(string placeholder, string? prefill) =>
        new() { PlaceholderText = placeholder, Text = prefill ?? "" };

    private static Control Labelled(string caption, TextBox field) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = caption, Foreground = Muted, FontSize = 11 },
            field,
        },
    };

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false; // survive focus loss — never drop a half-filled form
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        _name.Focus();
        _name.SelectAll();
    }

    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { }

    // ── Behaviour ──────────────────────────────────────────────────────────────────

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
        _onSave(new CustomCommand(name, target, Optional(_args), Optional(_workDir)));
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    private static string? Optional(TextBox box)
    {
        string v = box.Text?.Trim() ?? "";
        return v.Length == 0 ? null : v;
    }

    private void Cancel() => _stage?.Back();
}
