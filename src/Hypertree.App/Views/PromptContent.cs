using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A single-field text prompt (name a snapshot / template / new desktop, or rename a desktop), hosted as
/// a <b>card</b> on the shared <see cref="OverlayStage"/> rather than as its own top-level window.
/// Presenting it is a content swap on the already-visible host — flash-free — and it floats over the live
/// map backdrop the stage draws. A title, a one-line explanation, and a text box; Enter (or the confirm
/// button) hands the trimmed text (never empty) to <c>onConfirm</c>, then returns to where the chain
/// started. The stage-content sibling of <see cref="PaletteContent"/>; the card reads as the same surface
/// family. Shared chrome, Esc, and button-arrow routing live in <see cref="CardContent"/>.
/// </summary>
internal sealed class PromptContent : CardContent
{
    private readonly Action<string> _onConfirm;
    private readonly bool _selectAll;
    private readonly TextBox _input;

    /// <param name="prefill">Seeds the field (the rename flow); null leaves it blank.</param>
    /// <param name="selectAll">Select the prefilled text on open, so the first keystroke replaces it.</param>
    public PromptContent(string title, string explanation, string placeholder,
                         Action<string> onConfirm, string confirmLabel = "Save",
                         string? prefill = null, bool selectAll = false)
        : base(confirmLabel)
    {
        _onConfirm = onConfirm;
        _selectAll = selectAll;

        _input = new TextBox { PlaceholderText = placeholder, Text = prefill ?? "" };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 },
                _input,
                ButtonRow(),
            },
        };
        Build(body, width: 380);
    }

    protected override void FocusInitial()
    {
        _input.Focus();
        if (_selectAll) _input.SelectAll(); // rename: first keystroke replaces the prefilled name
    }

    protected override bool TryApply()
    {
        string n = _input.Text?.Trim() ?? "";
        if (n.Length == 0) return false; // require a name
        _onConfirm(n);
        return true;
    }
}
