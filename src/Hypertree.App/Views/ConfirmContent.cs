using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A yes/no confirm prompt, hosted as a <b>card</b> on the shared <see cref="OverlayStage"/> (the
/// stage-content successor of the old <c>ConfirmDialog</c> window — no separate window, no flash, the
/// live map reading behind it). Runs <c>onConfirm</c> only if the user accepts, then returns to where
/// the chain started; Esc / Cancel steps back one level.
///
/// The two choices are self-drawn <see cref="PromptButton"/>s (the themed <see cref="Button"/> renders
/// dark-on-dark and gives us no control over the focus cue). Cancel takes focus on open — a stray Enter
/// on a destructive prompt cancels rather than commits — and the focused button wears a blue ring. Move
/// between them with ←/→ (or ↑/↓) as well as Tab; Enter or Space fires the focused one.
/// </summary>
internal sealed class ConfirmContent : IStageContent
{
    private static readonly IBrush CardBg = Palette.CardBgBrush;
    private static readonly IBrush CardStroke = Palette.StrokeBrush;

    private readonly Action _onConfirm;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _done;

    public ConfirmContent(string message, Action onConfirm, string confirmLabel = "Delete")
    {
        _onConfirm = onConfirm;

        _ok = new PromptButton(confirmLabel);
        _ok.Invoked += Confirm;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 360, Padding = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
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
        };
        _root = new Grid { Children = { card } };

        // Tunnel Esc so it wins before a focused button; bubble arrows shuttle focus once a button is focused.
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false; // a focus loss mustn't silently confirm or cancel
    public bool DismissOnClickAway => false;  // a stray background click mustn't either

    public void OnPresented(OverlayStage stage) { _stage = stage; _done = false; _cancel.Focus(); } // safe default: Cancel
    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { } // handled by the _root handlers

    // ── Behaviour ──────────────────────────────────────────────────────────────────

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
    }

    private void OnBubbleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (!_ok.IsFocused && !_cancel.IsFocused)) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: _cancel.Focus(); e.Handled = true; break;
            case Key.Right or Key.Down: _ok.Focus(); e.Handled = true; break;
        }
    }

    private void Confirm()
    {
        if (_done) return;
        _done = true;
        _onConfirm();
        if (_stage?.Current == this) _stage.CompleteToBase(); // unless onConfirm swapped in new content
    }

    private void Cancel() => _stage?.Back();
}
