using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>Result of the new-branch prompt: a branch name and its ordered desktop labels.</summary>
internal sealed record BranchSpec(string Name, IReadOnlyList<string> Labels);

/// <summary>
/// A prompt for defining a branch — its name and a comma-separated list of desktop labels — hosted as a
/// <b>card</b> on the shared <see cref="OverlayStage"/> (the stage-content successor of the old
/// <c>BranchDialog</c> window: no separate window, no flash, the live map reading behind it). Raises the
/// parsed <see cref="BranchSpec"/> via <c>onConfirm</c>, then returns to where the chain started; Esc /
/// Cancel steps back one level.
/// </summary>
internal sealed class BranchContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    private readonly Action<BranchSpec> _onConfirm;
    private readonly TextBox _name;
    private readonly TextBox _labels;
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    /// <param name="prefillLabels">When supplied (the template flow), seeds the desktop-labels box with
    /// these — still fully editable. Null leaves it blank (the "Blank branch" flow).</param>
    public BranchContent(Action<BranchSpec> onConfirm, IReadOnlyList<string>? prefillLabels = null)
    {
        _onConfirm = onConfirm;

        // The name is always typed per-branch; labels are prefilled from a template when one was picked.
        _name = new TextBox { PlaceholderText = "branch name (e.g. feat-123)" };
        _labels = new TextBox
        {
            PlaceholderText = "desktop labels, comma-separated (e.g. SPA, API)",
            Text = prefillLabels is null ? "" : string.Join(", ", prefillLabels),
        };
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        _labels.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };

        _ok = new PromptButton("Create");
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
                    new TextBlock { Text = "Define a branch", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Provisions one virtual desktop per label and adds them as a new branch in the stack.",
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
    public bool DismissOnDeactivate => false; // never drop a half-typed branch on focus loss
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage) { _stage = stage; _submitted = false; _name.Focus(); }
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
        if (n.Length == 0 || parsed.Length == 0) return; // require both
        _submitted = true;
        _onConfirm(new BranchSpec(n, parsed));
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    private void Cancel() => _stage?.Back();
}
