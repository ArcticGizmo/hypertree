using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A self-drawn, keyboard-focusable button for the app's dark prompt surfaces. The themed
/// <see cref="Button"/> renders dark-on-dark on these cards and gives no control over the focus cue, so
/// prompts (confirm, name) use this instead: a neutral fill with a hover-brighten, and a blue focus ring
/// so the (Tab/arrow-)focused choice is unmistakable. Enter, Space, or a click raises <see cref="Invoked"/>.
/// Arrow navigation between several buttons is the host prompt's job — arrows aren't consumed here, so
/// they bubble up to the window.
/// </summary>
internal sealed class PromptButton : Border
{
    public event Action? Invoked;

    private static readonly Color Bg = Color.Parse("#2A3444"), BgHover = Color.Parse("#37455B");
    private static readonly Color Edge = Color.Parse("#3C4A5E"), Focused = Palette.Accent;
    private static readonly IBrush Ink = Palette.InkBrush;

    private bool _hover;

    public PromptButton(string text)
    {
        Focusable = true;
        Background = new SolidColorBrush(Bg);
        BorderBrush = new SolidColorBrush(Edge);
        BorderThickness = new Thickness(2); // constant, so shuttling focus never nudges the layout
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16, 7);
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        PointerEntered += (_, _) => { _hover = true; Repaint(); };
        PointerExited += (_, _) => { _hover = false; Repaint(); };
        PointerPressed += (_, e) => { e.Handled = true; Focus(); Invoked?.Invoke(); };
        GotFocus += (_, _) => Repaint();
        LostFocus += (_, _) => Repaint();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Invoked?.Invoke(); }
    }

    // Focus wins the border (blue ring); hover just brightens the fill. Both cues can coexist.
    private void Repaint()
    {
        Background = new SolidColorBrush(_hover ? BgHover : Bg);
        BorderBrush = new SolidColorBrush(IsFocused ? Focused : Edge);
    }
}
