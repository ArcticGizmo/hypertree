using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// Drawing primitives shared by the map themes — the colour maths and the per-cell click/activate overlay
/// that the metro, ASCII and spatial painters were each carrying their own byte-identical copy of. Pure
/// helpers with no layout or state: a theme still owns its own glyph, but the arithmetic that must stay in
/// lock-step across themes (the branch palette, the opaque blend, the recede-toward-ground dim) and the
/// pointer plumbing live in exactly one place.
/// </summary>
/// <remarks>
/// The board theme keeps its own fixed colours and bakes its click into the tile (it carries a delete badge
/// whose press must win over the tile), so it deliberately doesn't route through here — this is the shared
/// surface for the themes that tint by branch index and overlay a transparent hit rect.
/// </remarks>
internal static class ScenePaint
{
    /// <summary>The branch/route colour ring: every theme that tints a timeline by its index draws from this
    /// same eight-hue palette, so a branch reads as the same colour whether it's a metro route, an ASCII line
    /// or (via its stored group colour) a spatial hull.</summary>
    public static readonly Color[] LinePalette =
    {
        Color.Parse("#F4795B"), Color.Parse("#5BC8F4"), Color.Parse("#7BD88F"), Color.Parse("#C99BF4"),
        Color.Parse("#F4C95B"), Color.Parse("#F45B9C"), Color.Parse("#63D6C4"), Color.Parse("#9CB2F4"),
    };

    /// <summary>The palette colour for a branch index, wrapping (and normalising negatives) so any index maps
    /// to a stable hue.</summary>
    public static Color BranchColour(int branchIndex)
        => LinePalette[((branchIndex % LinePalette.Length) + LinePalette.Length) % LinePalette.Length];

    /// <summary>Opaque linear blend from <paramref name="a"/> to <paramref name="b"/> by <paramref name="t"/>
    /// (0 → a, 1 → b). Alpha is forced opaque: these are overlay colours drawn over the live desktop, so an
    /// element recedes by moving toward the ground, never by going translucent and bleeding the desktop
    /// through.</summary>
    public static Color Lerp(Color a, Color b, double t)
    {
        byte M(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
        return Color.FromArgb(0xFF, M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    /// <summary>Dim <paramref name="c"/> toward a theme's opaque <paramref name="ground"/> by
    /// <paramref name="t"/> — the colour-based recede a resting timeline uses in place of opacity. Each theme
    /// passes its own ground (the metro and ASCII grounds differ), so the exact tone is unchanged.</summary>
    public static Color Toward(Color ground, Color c, double t) => Lerp(ground, c, t);

    /// <summary>Add a transparent, hand-cursored hit rect over <paramref name="rect"/> that routes a single
    /// click to <paramref name="onClick"/> and a double click to <paramref name="onActivate"/> — the per-cell
    /// pointer plumbing the ASCII, metro and spatial maps each duplicated. A null callback means "not that
    /// gesture"; with both null nothing is added. The caller adds it last so it sits atop the glyph.</summary>
    public static void HitCell(Canvas canvas, Rect rect, Action? onClick, Action? onActivate)
    {
        if (onClick is null && onActivate is null) return;
        var hit = new Border
        {
            Width = rect.Width, Height = rect.Height, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        hit.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2) onActivate?.Invoke();
            else onClick?.Invoke();
        };
        Canvas.SetLeft(hit, rect.X);
        Canvas.SetTop(hit, rect.Y);
        canvas.Children.Add(hit);
    }
}
