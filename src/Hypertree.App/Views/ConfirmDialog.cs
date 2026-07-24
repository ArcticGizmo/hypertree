using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>A tiny yes/no prompt. Raises <see cref="Confirmed"/> only if the user accepts. Built on
/// <see cref="OverlayPrompt"/>, so it's a persistent, pinned, top-most surface that survives desktop
/// switches rather than a losable dialog.</summary>
internal sealed class ConfirmDialog : OverlayPrompt
{
    public event Action? Confirmed;

    private readonly Button _ok;
    protected override Control? InitialFocus => _ok;

    public ConfirmDialog(string message, IForegroundActivator activator, IDesktopController desktops,
                         string confirmLabel = "Delete")
        : base(activator, desktops)
    {
        Title = "Confirm";

        _ok = new Button { Content = confirmLabel, IsDefault = true };
        _ok.Click += (_, _) => { Confirmed?.Invoke(); Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        SetCard(new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 360, Padding = new Thickness(18),
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
                        Children = { cancel, _ok },
                    },
                },
            },
        });
    }
}
