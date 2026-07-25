using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A single-field name prompt, hosted on the shared <see cref="OverlayStage"/> rather than as its own
/// top-level window. Presenting it is a content swap on the already-visible host — so summoning it from
/// the command palette (e.g. "Snapshot layout…") is flash-free, where the old <see cref="NameDialog"/>
/// window tore the stage down and built a fresh window up. A title, a one-line explanation, and a text
/// box; Enter (or the confirm button) raises <see cref="OverlayStage"/>-dismiss after handing the trimmed
/// text (never empty) to <c>onConfirm</c>. The stage-content sibling of <see cref="PaletteContent"/>; the
/// card mirrors <see cref="NameDialog"/> so the two read as the same surface family.
///
/// Like the map and move — and unlike the palettes — it never dismisses on lost focus or a background
/// click, so a half-typed name can't vanish out from under you. Esc cancels (or returns to <c>onBack</c>).
/// </summary>
internal sealed class PromptContent : IStageContent
{
    // Matches NameDialog / PaletteContent so prompts read as one surface family.
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly Action<string> _onConfirm;
    private readonly Action? _onBack; // Esc: return to the surface we opened over (else dismiss the stage)

    private readonly TextBox _input;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    public PromptContent(string title, string explanation, string placeholder,
                         Action<string> onConfirm, string confirmLabel = "Save", Action? onBack = null)
    {
        _onConfirm = onConfirm;
        _onBack = onBack;

        _input = new TextBox { PlaceholderText = placeholder };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };

        _ok = new PromptButton(confirmLabel);
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 380, Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
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
        };
        _root = new Grid { Children = { card } };

        // Tunnel Esc so it wins before the text box; leave Enter to the field / focused button so Enter on
        // Cancel doesn't submit. Arrows shuttle button focus but only once a button is focused (bubble,
        // guarded), so they never hijack the caret while you type.
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public bool Dim => true; // dim backdrop + centred card, like the previewed palettes
    public bool DismissOnDeactivate => false; // survive a desktop switch / focus loss — never drop a half-typed name
    public bool DismissOnClickAway => false;  // a background click mustn't discard the prompt either

    public void OnPresented(OverlayStage stage) { _stage = stage; _input.Focus(); }
    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { } // handled by the _root handlers

    // ── Behaviour ──────────────────────────────────────────────────────────────────

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
    }

    // Arrow keys shuttle focus between the buttons — but only once a button already has focus, so they
    // never hijack caret movement while you're typing in the field.
    private void OnBubbleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (!_ok.IsFocused && !_cancel.IsFocused)) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: _cancel.Focus(); e.Handled = true; break;
            case Key.Right or Key.Down: _ok.Focus(); e.Handled = true; break;
        }
    }

    private void Submit()
    {
        if (_submitted) return;
        string n = _input.Text?.Trim() ?? "";
        if (n.Length == 0) return; // require a name
        _submitted = true;
        _onConfirm(n);
        if (_stage?.Current == this) _stage.Dismiss(); // unless onConfirm already swapped in new content
    }

    private void Cancel()
    {
        if (_onBack is not null) _onBack(); // swaps this out for the surface we came from
        else _stage?.Dismiss();
    }
}
