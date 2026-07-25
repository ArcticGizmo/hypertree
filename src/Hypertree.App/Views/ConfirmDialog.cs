using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>A tiny yes/no prompt. Raises <see cref="Confirmed"/> only if the user accepts. Built on
/// <see cref="OverlayPrompt"/>, so it's a persistent, pinned, top-most surface that survives desktop
/// switches rather than a losable dialog.
///
/// The two choices are self-drawn <see cref="PromptButton"/>s (the themed <see cref="Button"/> renders
/// dark-on-dark and gives us no control over the focus cue). Cancel takes focus on open — a stray Enter
/// on a destructive prompt cancels rather than commits — and the focused button wears a blue ring. Move
/// between them with ←/→ (or ↑/↓) as well as Tab; Enter or Space fires the focused one.</summary>
internal sealed class ConfirmDialog : OverlayPrompt
{
    public event Action? Confirmed;

    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    protected override Control? InitialFocus => _cancel; // safe default: focus Cancel, not the destructive action

    public ConfirmDialog(string message, IForegroundActivator activator, IDesktopController desktops,
                         string confirmLabel = "Delete")
        : base(activator, desktops)
    {
        Title = "Confirm";

        _ok = new PromptButton(confirmLabel);
        _ok.Invoked += () => { Confirmed?.Invoke(); Close(); };
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Close;

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
                        Children = { _cancel, _ok }, // Cancel on the left, so ←/→ maps left→Cancel, right→confirm
                    },
                },
            },
        });
    }

    // Arrow keys shuttle focus between the two buttons (Tab already does, via the framework). The buttons
    // themselves consume Enter/Space; arrows aren't handled there, so they bubble up to here.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); // Esc → Close (OverlayPrompt)
        if (e.Handled || (!_ok.IsFocused && !_cancel.IsFocused)) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: _cancel.Focus(); e.Handled = true; break;
            case Key.Right or Key.Down: _ok.Focus(); e.Handled = true; break;
        }
    }
}
