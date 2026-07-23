using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Hypertree.App.Views;

/// <summary>Result of the new-group dialog: a group name and its ordered desktop labels.</summary>
internal sealed record ScopeSpec(string Name, IReadOnlyList<string> Labels);

/// <summary>
/// A tiny modal-less prompt for defining a group: its name and a comma-separated list of desktop
/// labels. Raises <see cref="Confirmed"/> with the parsed spec, then closes. M1's stand-in for the
/// M2 git-worktree-driven flow — enough to feel group creation.
/// </summary>
internal sealed class ScopeDialog : Window
{
    public event Action<ScopeSpec>? Confirmed;

    public ScopeDialog()
    {
        Title = "New group";
        RequestedThemeVariant = ThemeVariant.Dark; // match the board/palette look
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Width = 380;
        Background = new SolidColorBrush(Color.Parse("#12161F"));

        // No prefilled defaults — you must type a name and at least one desktop label for Create.
        var name = new TextBox { PlaceholderText = "scope name (e.g. feat-123)" };
        var labels = new TextBox { PlaceholderText = "desktop labels, comma-separated (e.g. SPA, API)" };

        var ok = new Button { Content = "Create", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            string n = name.Text?.Trim() ?? "";
            var parsed = (labels.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (n.Length == 0 || parsed.Length == 0) return; // require both
            Confirmed?.Invoke(new ScopeSpec(n, parsed));
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Define a group", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Provisions one virtual desktop per label and adds them as a new group in the stack.",
                                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#999")), FontSize = 12 },
                new TextBlock { Text = "Name", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) },
                name,
                new TextBlock { Text = "Desktops", FontSize = 12 },
                labels,
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
