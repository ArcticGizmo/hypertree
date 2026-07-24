using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Hypertree.App.Views;

/// <summary>
/// A tiny single-field prompt: a title, a one-line explanation, and a text box. Raises
/// <see cref="Confirmed"/> with the trimmed text (never empty), then closes. Same dark look as the
/// group dialog / palette. Used for naming a snapshot.
/// </summary>
internal sealed class NameDialog : Window
{
    public event Action<string>? Confirmed;

    public NameDialog(string title, string explanation, string placeholder, string confirmLabel = "Save")
    {
        Title = title;
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Width = 380;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        var input = new TextBox { PlaceholderText = placeholder };

        var ok = new Button { Content = confirmLabel, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            string n = input.Text?.Trim() ?? "";
            if (n.Length == 0) return; // require a name
            Confirmed?.Invoke(n);
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Color.Parse("#999")), FontSize = 12 },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
                    Children = { cancel, ok },
                },
            },
        };
    }
}
