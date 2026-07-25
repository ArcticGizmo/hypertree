using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>
/// A minimal rename prompt: a title and a single text box, prefilled with the desktop's current name and
/// all-selected so the first keystroke replaces it. No explicit buttons — Enter confirms (raising
/// <see cref="Confirmed"/> with the trimmed, non-empty text), Esc cancels. Built on
/// <see cref="OverlayPrompt"/> so it force-foregrounds with the field focused immediately and survives
/// desktop switches rather than being a losable dialog.
/// </summary>
internal sealed class RenameDialog : OverlayPrompt
{
    public event Action<string>? Confirmed;

    private readonly TextBox _input;
    protected override Control? InitialFocus => _input;

    public RenameDialog(string currentName, IForegroundActivator activator, IDesktopController desktops)
        : base(activator, desktops)
    {
        Title = "Rename desktop";

        _input = new TextBox { Text = currentName };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            string n = _input.Text?.Trim() ?? "";
            if (n.Length > 0) { Confirmed?.Invoke(n); Close(); } // empty is a no-op — keep the prompt up
            e.Handled = true;
        };
        // Base focuses the field on open (see OverlayPrompt.OnShown); select-all so typing overwrites.
        Opened += (_, _) => _input.SelectAll();

        SetCard(new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 360, Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Rename desktop", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Type a new name · ↵ rename · Esc cancel",
                                    Foreground = Muted, FontSize = 12 },
                    _input,
                },
            },
        });
    }
}
