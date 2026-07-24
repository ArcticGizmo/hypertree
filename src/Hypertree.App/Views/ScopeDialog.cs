using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>Result of the new-group dialog: a group name and its ordered desktop labels.</summary>
internal sealed record ScopeSpec(string Name, IReadOnlyList<string> Labels);

/// <summary>
/// A prompt for defining a group: its name and a comma-separated list of desktop labels. Raises
/// <see cref="Confirmed"/> with the parsed spec, then closes. Built on <see cref="OverlayPrompt"/>, so
/// it's a persistent, pinned, top-most surface that survives desktop switches rather than a losable
/// dialog. M1's stand-in for the M2 git-worktree-driven flow — enough to feel group creation.
/// </summary>
internal sealed class ScopeDialog : OverlayPrompt
{
    public event Action<ScopeSpec>? Confirmed;

    private readonly TextBox _name;
    protected override Control? InitialFocus => _name;

    /// <param name="prefillLabels">When supplied (the template flow), seeds the desktop-labels box with
    /// these — still fully editable. Null leaves it blank (the "Blank group" flow).</param>
    public ScopeDialog(IForegroundActivator activator, IDesktopController desktops,
                       IReadOnlyList<string>? prefillLabels = null)
        : base(activator, desktops)
    {
        Title = "New group";

        // The name is always typed per-group; labels are prefilled from a template when one was picked.
        _name = new TextBox { PlaceholderText = "scope name (e.g. feat-123)" };
        var labels = new TextBox
        {
            PlaceholderText = "desktop labels, comma-separated (e.g. SPA, API)",
            Text = prefillLabels is null ? "" : string.Join(", ", prefillLabels),
        };

        var ok = new Button { Content = "Create", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            string n = _name.Text?.Trim() ?? "";
            var parsed = (labels.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (n.Length == 0 || parsed.Length == 0) return; // require both
            Confirmed?.Invoke(new ScopeSpec(n, parsed));
            Close();
        };
        cancel.Click += (_, _) => Close();

        SetCard(new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 380, Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Define a group", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Provisions one virtual desktop per label and adds them as a new group in the stack.",
                                    TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 },
                    new TextBlock { Text = "Name", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) },
                    _name,
                    new TextBlock { Text = "Desktops", FontSize = 12 },
                    labels,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancel, ok },
                    },
                },
            },
        });
    }
}
