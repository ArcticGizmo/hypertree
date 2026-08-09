using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// The app's shared dark palette — the single source of truth for the handful of colours that recur across
/// almost every surface (the map, the cards, the switcher, the settings window, the scene painters). Each
/// is offered as both a <see cref="Color"/> (the scene painters compose with these) and a cached
/// <see cref="IBrush"/> (the control-tree classes assign these). Re-skinning is a change here, not a
/// find-replace across a dozen files; the previous per-file constants had drifted to several names for the
/// same hex (Fg/Ink, Muted/Dim/FgDim, Accent/Focus).
/// </summary>
/// <remarks>
/// Named <c>Palette</c> rather than <c>Theme</c> because Avalonia's <c>StyledElement.Theme</c> is an
/// inherited instance member on every Window/Control, which would shadow a static <c>Theme</c> in their
/// field initializers. Deliberately narrow: only the genuinely-shared core lives here. One-off and semantic
/// colours (the per-branch line palette, warning ambers, the metro/ascii backgrounds) stay local to the
/// file that owns them — hoisting those would be a false shared-ness that couples unrelated surfaces.
/// </remarks>
internal static class Palette
{
    public static readonly Color Ink    = Color.Parse("#E8EDF5"); // primary foreground / text
    public static readonly Color Muted  = Color.Parse("#9AA6B8"); // secondary, de-emphasised foreground
    public static readonly Color Accent = Color.Parse("#6EA8FF"); // selection / focus (blue)
    public static readonly Color Here   = Color.Parse("#34D399"); // the desktop you're on (green)
    public static readonly Color Stroke = Color.Parse("#2A3444"); // borders, dividers, tile edges
    public static readonly Color CardBg = Color.Parse("#12161F"); // card / panel background

    public static readonly IBrush InkBrush    = new SolidColorBrush(Ink);
    public static readonly IBrush MutedBrush  = new SolidColorBrush(Muted);
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush StrokeBrush = new SolidColorBrush(Stroke);
    public static readonly IBrush CardBgBrush = new SolidColorBrush(CardBg);
}
