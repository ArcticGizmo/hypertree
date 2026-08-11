using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Desktops;

namespace Hypertree.App.Views;

/// <summary>
/// Shared base for the centred prompt <b>cards</b> hosted on the <see cref="OverlayStage"/> — confirm,
/// single-/multi-field prompts, the branch/template definers, and the custom-command form. It owns the
/// parts every card had copied verbatim: the card chrome (rounded, bordered, centred), the Cancel/OK
/// buttons, the key routing (Esc steps back; ← ↑ / → ↓ shuttle focus between the two buttons once one is
/// focused, so they never hijack the caret while typing), the single-shot submit guard, and the
/// "run then unwind to where the chain started" completion.
/// </summary>
/// <remarks>
/// A subclass builds its own field stack and calls <see cref="Build"/> from its constructor (the base can't
/// build the card itself — a virtual call from the base constructor would run before the subclass's fields
/// are assigned). It then implements <see cref="FocusInitial"/> and <see cref="TryApply"/>. This replaces
/// five near-identical hand-rolled cards that had already drifted (Left/Right-only arrows in one, several
/// widths, a stray #999 muted).
/// </remarks>
internal abstract class CardContent : IStageContent
{
    protected static readonly IBrush CardBg = Palette.CardBgBrush;
    protected static readonly IBrush CardStroke = Palette.StrokeBrush;
    // The card explanations' muted grey. Slightly lighter than Palette.Muted by long-standing intent on
    // these prompts; kept as-is so the extraction doesn't restyle them.
    protected static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));

    protected readonly PromptButton Ok;
    protected readonly PromptButton CancelButton;

    private Control? _root;
    private OverlayStage? _stage;
    private bool _done;

    protected CardContent(string okLabel)
    {
        Ok = new PromptButton(okLabel);
        Ok.Invoked += Commit;
        CancelButton = new PromptButton("Cancel");
        CancelButton.Invoked += Cancel;
    }

    /// <summary>The stage this card is presented on, or null before it's shown.</summary>
    protected OverlayStage? Stage => _stage;

    /// <summary>
    /// Wrap the subclass's <paramref name="body"/> (its title / explanation / fields, and the Cancel/OK row
    /// it places using <see cref="Ok"/> / <see cref="CancelButton"/>) in the shared card chrome and wire the
    /// key handlers. Call once, at the end of the subclass constructor.
    /// </summary>
    protected void Build(Control body, double width, double padding = 16)
    {
        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = width, Padding = new Thickness(padding),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = body,
        };
        _root = new Grid { Children = { card } };
        // Tunnel Esc so it wins before a focused field/button; bubble arrows shuttle button focus.
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnBubbleKey, RoutingStrategies.Bubble);
    }

    /// <summary>The right-aligned Cancel / OK row every card ends with. <paramref name="topMargin"/> is the
    /// gap above it on top of the body's own spacing (0 when the body's spacing already separates it).</summary>
    protected StackPanel ButtonRow(double topMargin = 8) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, topMargin, 0, 0),
        Children = { CancelButton, Ok }, // Cancel on the left → ←/→ maps left→Cancel, right→confirm
    };

    /// <summary>Put focus where the card wants it on (re)present — usually the first field, or Cancel for a
    /// destructive confirm.</summary>
    protected abstract void FocusInitial();

    /// <summary>Validate and, if valid, run the card's action (the <c>onConfirm</c> callback). Return true
    /// when it ran (the card then unwinds to where the chain started), false to stay open for more input.</summary>
    protected abstract bool TryApply();

    /// <summary>Extra tunnel-phase keys beyond Esc — e.g. a card that submits on Ctrl+Enter. Default none.</summary>
    protected virtual void OnExtraTunnelKey(KeyEventArgs e) { }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root!;
    public StageLayer Layer => StageLayer.Card;

    /// <summary>Rooms to spotlight behind this card (see <see cref="IStageContent.BackdropSpotlight"/>). None
    /// by default; a destructive confirm overrides this to pick out what it's about to delete.</summary>
    public virtual IReadOnlyCollection<DesktopId>? BackdropSpotlight() => null;
    public bool DismissOnDeactivate => false; // a focus loss must never silently confirm, cancel, or drop input
    public bool DismissOnClickAway => false;  // nor a stray background click

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _done = false; // re-armed each time we're (re)shown
        FocusInitial();
    }

    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { } // handled by the _root handlers

    // ── Behaviour ──────────────────────────────────────────────────────────────────

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else OnExtraTunnelKey(e);
    }

    private void OnBubbleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (!Ok.IsFocused && !CancelButton.IsFocused)) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: CancelButton.Focus(); e.Handled = true; break;
            case Key.Right or Key.Down: Ok.Focus(); e.Handled = true; break;
        }
    }

    /// <summary>Submit: single-shot (a second Enter/click is ignored), runs <see cref="TryApply"/>, and on
    /// success unwinds to the base surface unless the action swapped in new content.</summary>
    protected void Commit()
    {
        if (_done || !TryApply()) return;
        _done = true;
        if (_stage?.Current == this) _stage.CompleteToBase();
    }

    protected void Cancel() => _stage?.Back(); // step back to the surface we opened over
}
