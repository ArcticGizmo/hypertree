using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// An informational message hosted as a <b>card</b> on the shared <see cref="OverlayStage"/> — the
/// single-button sibling of <see cref="ConfirmContent"/>. Used for stage results that need no decision
/// (an update check that came back "up to date" / "not available" / "failed", or a transient
/// "Checking…" while an async action runs). Esc, a background click, or the OK button steps back one
/// level; a null <paramref name="dismissLabel"/> shows no button (for progress cards that dismiss only
/// on Esc / click-away).
/// </summary>
internal sealed class NoticeContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));

    private readonly PromptButton? _ok;
    private readonly Control _root;
    private OverlayStage? _stage;

    public NoticeContent(string message, string? dismissLabel = "OK")
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#E8EDF5")), FontSize = 14,
        });

        if (dismissLabel is not null)
        {
            _ok = new PromptButton(dismissLabel);
            _ok.Invoked += () => _stage?.Back();
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { _ok },
            });
        }

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 360, Padding = new Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
        _root = new Grid { Children = { card } };

        // Tunnel Esc so it steps back before any focused button consumes it.
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
    }

    // ── IStageContent ────────────────────────────────────────────────────────────

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false; // survive the deactivation a desktop switch causes
    public bool DismissOnClickAway => true;   // click the board to dismiss an info card

    public void OnPresented(OverlayStage stage) { _stage = stage; _ok?.Focus(); }
    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { }

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _stage?.Back(); e.Handled = true; }
    }
}
