using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A prompt for defining a <b>branch template</b> — a name plus a comma-separated list of desktop labels —
/// hosted as a <b>card</b> on the shared <see cref="OverlayStage"/>. The two-field sibling of
/// <see cref="PromptContent"/> (which is single-field) and the template counterpart of
/// <see cref="BranchContent"/> (which provisions real desktops; this only records a reusable recipe).
/// Raises the trimmed name + parsed labels via <c>onConfirm</c>, then returns to where the chain started.
/// Shared chrome, Esc, and button-arrow routing live in <see cref="CardContent"/>.
/// </summary>
internal sealed class TemplateContent : CardContent
{
    private readonly Action<string, IReadOnlyList<string>> _onConfirm;
    private readonly TextBox _name;
    private readonly TextBox _labels;

    /// <param name="prefillName">Seeds the name box — the "type a new name in the search box" flow passes
    /// the typed text here. Null leaves it blank.</param>
    /// <param name="prefillLabels">Seeds the desktop-labels box; null leaves it blank.</param>
    public TemplateContent(Action<string, IReadOnlyList<string>> onConfirm,
                           string? prefillName = null, IReadOnlyList<string>? prefillLabels = null)
        : base("Save")
    {
        _onConfirm = onConfirm;

        _name = new TextBox { PlaceholderText = "template name (e.g. Fullstack)", Text = prefillName ?? "" };
        _labels = new TextBox
        {
            PlaceholderText = "desktop labels, comma-separated (e.g. SPA, API)",
            Text = prefillLabels is null ? "" : string.Join(", ", prefillLabels),
        };
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        _labels.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "New template", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "A reusable set of desktops you can drop into a new branch. Name it and list its desktops.",
                                TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 },
                new TextBlock { Text = "Name", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) },
                _name,
                new TextBlock { Text = "Desktops", FontSize = 12 },
                _labels,
                ButtonRow(),
            },
        };
        Build(body, width: 380);
    }

    protected override void FocusInitial()
    {
        // Land the caret where there's still work to do: the labels box when the name was prefilled, else name.
        if ((_name.Text ?? "").Length > 0) _labels.Focus(); else _name.Focus();
    }

    protected override bool TryApply()
    {
        string n = _name.Text?.Trim() ?? "";
        var parsed = (_labels.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n.Length == 0 || parsed.Length == 0) return false; // require both a name and at least one desktop
        _onConfirm(n, parsed);
        return true;
    }
}
