using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The add/edit form for a launcher <see cref="CustomCommand"/>, hosted as a <b>card</b> on the shared
/// <see cref="OverlayStage"/> — the multi-field sibling of <see cref="PromptContent"/>. Four fields: a
/// display name and a shell target (both required), plus optional arguments and a working directory. On
/// save it hands back a built <see cref="CustomCommand"/> (blank optionals folded to null) and unwinds via
/// <see cref="OverlayStage.CompleteToBase"/>. Ctrl+Enter submits from any field. Shared chrome, Esc, and
/// button-arrow routing live in <see cref="CardContent"/>.
/// </summary>
internal sealed class CustomCommandContent : CardContent
{
    private readonly Action<CustomCommand> _onSave;
    private readonly TextBox _name;
    private readonly TextBox _target;
    private readonly TextBox _args;
    private readonly TextBox _workDir;

    /// <param name="seed">Prefills the fields — the command being edited, or one carrying just a typed-in
    /// name for a fresh add. Null starts blank.</param>
    /// <param name="isEdit">Titles the card "Edit"/"Add" and the button "Save"/"Add". Kept separate from
    /// <paramref name="seed"/> so a name-prefilled add still reads as an add.</param>
    public CustomCommandContent(Action<CustomCommand> onSave, CustomCommand? seed = null, bool isEdit = false)
        : base(isEdit ? "Save" : "Add")
    {
        _onSave = onSave;
        CustomCommand? existing = seed;

        _name = Field("e.g. Open work email", existing?.Name);
        _target = Field(@"app, file, folder or URL — e.g. https://mail.google.com", existing?.Target);
        _args = Field("optional arguments", existing?.Arguments);
        _workDir = Field(@"optional working directory — e.g. C:\projects", existing?.WorkingDirectory);

        var body = new StackPanel
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
                ButtonRow(topMargin: 10),
            },
        };
        Build(body, width: 440);
    }

    private static TextBox Field(string placeholder, string? prefill) =>
        new() { PlaceholderText = placeholder, Text = prefill ?? "" };

    private static Control Labelled(string caption, TextBox field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Muted, FontSize = 11 }, field },
    };

    protected override void FocusInitial() { _name.Focus(); _name.SelectAll(); }

    // Ctrl+Enter submits from any field (a bare Enter just moves within a field / activates a focused button).
    protected override void OnExtraTunnelKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Commit(); e.Handled = true; }
    }

    protected override bool TryApply()
    {
        string name = _name.Text?.Trim() ?? "";
        string target = _target.Text?.Trim() ?? "";
        if (name.Length == 0 || target.Length == 0) return false; // name + target are both required
        _onSave(new CustomCommand(name, target, Optional(_args), Optional(_workDir)));
        return true;
    }

    private static string? Optional(TextBox box)
    {
        string v = box.Text?.Trim() ?? "";
        return v.Length == 0 ? null : v;
    }
}
