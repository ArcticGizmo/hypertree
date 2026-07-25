using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>
/// A tiny single-field prompt: a title, a one-line explanation, and a text box. Raises
/// <see cref="Confirmed"/> with the trimmed text (never empty), then closes. Built on
/// <see cref="OverlayPrompt"/>, so it's a persistent, pinned, top-most surface that survives desktop
/// switches rather than a losable dialog. Used for naming a snapshot or a branch template.
/// </summary>
internal sealed class NameDialog : OverlayPrompt
{
    public event Action<string>? Confirmed;

    private readonly TextBox _input;
    protected override Control? InitialFocus => _input;

    public NameDialog(string title, string explanation, string placeholder,
                      IForegroundActivator activator, IDesktopController desktops, string confirmLabel = "Save")
        : base(activator, desktops)
    {
        Title = title;
        _input = new TextBox { PlaceholderText = placeholder };

        var ok = new Button { Content = confirmLabel, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            string n = _input.Text?.Trim() ?? "";
            if (n.Length == 0) return; // require a name
            Confirmed?.Invoke(n);
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
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap,
                                    Foreground = Muted, FontSize = 12 },
                    _input,
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
