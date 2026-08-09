using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// A prompt for defining a <b>branch template</b> — a name plus a comma-separated list of desktop labels —
/// hosted as a <b>card</b> on the shared <see cref="OverlayStage"/>. The two-field sibling of
/// <see cref="PromptContent"/> (which is single-field) and the template counterpart of
/// <see cref="BranchContent"/> (which provisions real desktops; this only records a reusable recipe).
/// Raises the trimmed name + parsed labels via <c>onConfirm</c>, then returns to where the chain started;
/// Esc / Cancel steps back one level. Like the other prompts it never dismisses on lost focus or a
/// background click, so a half-typed template can't vanish.
/// </summary>
internal sealed class TemplateContent : IStageContent
{
    private static readonly IBrush CardBg = Palette.CardBgBrush;
    private static readonly IBrush CardStroke = Palette.StrokeBrush;
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly Action<string, IReadOnlyList<string>> _onConfirm;
    private readonly TextBox _name;
    private readonly TextBox _labels;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    /// <param name="prefillName">Seeds the name box — the "type a new name in the search box" flow passes
    /// the typed text here. Null leaves it blank.</param>
    /// <param name="prefillLabels">Seeds the desktop-labels box; null leaves it blank.</param>
    public TemplateContent(Action<string, IReadOnlyList<string>> onConfirm,
                           string? prefillName = null, IReadOnlyList<string>? prefillLabels = null)
    {
        _onConfirm = onConfirm;

        _name = new TextBox { PlaceholderText = "template name (e.g. Fullstack)", Text = prefillName ?? "" };
        _labels = new TextBox
        {
            PlaceholderText = "desktop labels, comma-separated (e.g. SPA, API)",
            Text = prefillLabels is null ? "" : string.Join(", ", prefillLabels),
        };
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        _labels.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };

        _ok = new PromptButton("Save");
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
                    new TextBlock { Text = "New template", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "A reusable set of desktops you can drop into a new branch. Name it and list its desktops.",
                                    TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 },
                    new TextBlock { Text = "Name", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) },
                    _name,
                    new TextBlock { Text = "Desktops", FontSize = 12 },
                    _labels,
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

        // Tunnel Esc so it wins before the fields; bubble arrows shuttle button focus once a button is focused.
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false; // never drop a half-typed template on focus loss
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        // Land the caret where there's still work to do: the labels box when the name was prefilled, else name.
        if ((_name.Text ?? "").Length > 0) _labels.Focus(); else _name.Focus();
    }
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

    private void Submit()
    {
        if (_submitted) return;
        string n = _name.Text?.Trim() ?? "";
        var parsed = (_labels.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n.Length == 0 || parsed.Length == 0) return; // require both a name and at least one desktop
        _submitted = true;
        _onConfirm(n, parsed);
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    private void Cancel() => _stage?.Back();
}
