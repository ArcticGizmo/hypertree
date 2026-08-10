using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>Result of the new-branch prompt: a branch name and its ordered desktop labels.</summary>
internal sealed record BranchSpec(string Name, IReadOnlyList<string> Labels);

/// <summary>
/// A prompt for defining a branch — its name and a comma-separated list of desktop labels — hosted as a
/// <b>card</b> on the shared <see cref="OverlayStage"/> (the stage-content successor of the old
/// <c>BranchDialog</c> window: no separate window, no flash, the live map reading behind it). Raises the
/// parsed <see cref="BranchSpec"/> via <c>onConfirm</c>, then returns to where the chain started. Shared
/// chrome, Esc, and button-arrow routing live in <see cref="CardContent"/>.
/// </summary>
internal sealed class BranchContent : CardContent
{
    /// <summary>What an empty Desktops box provisions: one desktop, so a branch is only ever a name away.</summary>
    private static readonly string[] DefaultLabels = { "default" };

    private readonly Action<BranchSpec> _onConfirm;
    private readonly Action<Action<IReadOnlyList<string>>>? _onLoadTemplate;
    private readonly TextBox _name;
    private readonly TextBox _labels;

    /// <param name="prefillLabels">When supplied, seeds the desktop-labels box with these — still fully
    /// editable. Null leaves it blank.</param>
    /// <param name="onLoadTemplate">When supplied, an in-card <b>Load from template</b> button is shown
    /// after the name field. Pressing it hands the callback a setter; the host opens a template picker and
    /// feeds the chosen template's labels back through the setter, which drops them into the (still
    /// editable) labels box. Null hides the button — the caller has no templates to offer.</param>
    public BranchContent(Action<BranchSpec> onConfirm, IReadOnlyList<string>? prefillLabels = null,
                         Action<Action<IReadOnlyList<string>>>? onLoadTemplate = null)
        : base("Create")
    {
        _onConfirm = onConfirm;
        _onLoadTemplate = onLoadTemplate;

        // The name is always typed per-branch; labels start blank (or prefilled) and can be filled from a
        // template via the Load button below.
        _name = new TextBox { PlaceholderText = "branch name (e.g. feat-123)" };
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
                new TextBlock { Text = "Define a branch", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Provisions one virtual desktop per label and adds them as a new branch in the stack. " +
                                       "Leave the desktops blank and you get one, called “default”.",
                                TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 },
                new TextBlock { Text = "Name", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) },
                _name,
            },
        };

        // Only offered when the host has templates to load — otherwise the button would be a dead end.
        if (_onLoadTemplate is not null)
        {
            var load = new PromptButton("Load from template  ▾") { HorizontalAlignment = HorizontalAlignment.Left };
            load.Invoked += LoadTemplate;
            body.Children.Add(load);
        }

        body.Children.Add(new TextBlock { Text = "Desktops (optional)", FontSize = 12 });
        body.Children.Add(_labels);
        body.Children.Add(ButtonRow());

        Build(body, width: 380);
    }

    protected override void FocusInitial() => _name.Focus();

    protected override bool TryApply()
    {
        string n = _name.Text?.Trim() ?? "";
        if (n.Length == 0) return false; // the name is the one thing we can't invent
        var parsed = (_labels.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Blank (or all-separators) Desktops box → a single "default" desktop, so naming a branch is
        // enough to stand one up; the labels are for when you already know how you'll split the work.
        _onConfirm(new BranchSpec(n, parsed.Length > 0 ? parsed : DefaultLabels));
        return true;
    }

    // Ask the host to pick a template, dropping the chosen desktop labels into the (still editable) box.
    // The picker is pushed over this card, so Esc / a pick returns here rather than restarting the flow.
    private void LoadTemplate() =>
        _onLoadTemplate?.Invoke(labels => _labels.Text = string.Join(", ", labels));
}
