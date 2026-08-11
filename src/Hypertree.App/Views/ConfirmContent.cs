using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;

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
/// between them with ←/→ (or ↑/↓) as well as Tab; Enter or Space fires the focused one (see
/// <see cref="CardContent"/> for the shared chrome and key routing).
/// </summary>
internal sealed class ConfirmContent : CardContent
{
    private readonly Action _onConfirm;
    private readonly IReadOnlyCollection<DesktopId>? _spotlight;

    /// <param name="spotlight">Desktops to pick out on the map behind the card — everything else dims hard, so
    /// a destructive confirm makes plain exactly what it will remove. Null leaves the backdrop at full
    /// brightness (non-map confirms: templates, layouts, custom commands).</param>
    public ConfirmContent(string message, Action onConfirm, string confirmLabel = "Delete",
                          IReadOnlyCollection<DesktopId>? spotlight = null)
        : base(confirmLabel)
    {
        _onConfirm = onConfirm;
        _spotlight = spotlight;

        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message, TextWrapping = TextWrapping.Wrap,
                    Foreground = Palette.InkBrush, FontSize = 14,
                },
                ButtonRow(topMargin: 0), // the body's own 14px spacing separates it
            },
        };
        Build(body, width: 360, padding: 18);
    }

    protected override void FocusInitial() => CancelButton.Focus(); // safe default: Cancel on a destructive prompt
    protected override bool TryApply() { _onConfirm(); return true; } // no validation — always runs

    public override IReadOnlyCollection<DesktopId>? BackdropSpotlight() => _spotlight;
}
