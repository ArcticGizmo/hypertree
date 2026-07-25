using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;
using Hypertree.Platform;

namespace Hypertree.App.Views;

/// <summary>
/// A tiny single-field prompt: a title, a one-line explanation, and a text box. Raises
/// <see cref="Confirmed"/> with the trimmed text (never empty), then closes. Built on
/// <see cref="OverlayPrompt"/>, so it's a persistent, pinned, top-most surface that survives desktop
/// switches rather than a losable dialog. Used for naming a snapshot, a branch template, or a new desktop.
///
/// The text box takes focus so you can type immediately; Enter confirms. The two choices are self-drawn
/// <see cref="PromptButton"/>s with a blue focus ring — reachable by Tab or, once a button is focused, the
/// ←/→ (↑/↓) arrows (arrows inside the text box still move the caret).
/// </summary>
internal sealed class NameDialog : OverlayPrompt
{
    public event Action<string>? Confirmed;

    private readonly TextBox _input;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    protected override Control? InitialFocus => _input;

    public NameDialog(string title, string explanation, string placeholder,
                      IForegroundActivator activator, IDesktopController desktops, string confirmLabel = "Save")
        : base(activator, desktops)
    {
        Title = title;
        _input = new TextBox { PlaceholderText = placeholder };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };

        _ok = new PromptButton(confirmLabel);
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Close;

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
                        Children = { _cancel, _ok }, // Cancel on the left, so ←/→ maps left→Cancel, right→confirm
                    },
                },
            },
        });
    }

    private void Submit()
    {
        string n = _input.Text?.Trim() ?? "";
        if (n.Length == 0) return; // require a name
        Confirmed?.Invoke(n);
        Close();
    }

    // Arrow keys shuttle focus between the buttons — but only once a button already has focus, so they
    // never hijack caret movement while you're typing in the field.
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
