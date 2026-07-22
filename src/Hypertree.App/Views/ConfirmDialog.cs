using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>A tiny modal-less yes/no prompt. Raises <see cref="Confirmed"/> only if the user accepts.</summary>
internal sealed class ConfirmDialog : Window
{
    public event Action? Confirmed;

    public ConfirmDialog(string message, string confirmLabel = "Delete")
    {
        Title = "Confirm";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Width = 360;
        Topmost = true;

        var ok = new Button { Content = confirmLabel, IsDefault = true };
        ok.Click += (_, _) => { Confirmed?.Invoke(); Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = message, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#E8EDF5")), FontSize = 14,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
    }
}
