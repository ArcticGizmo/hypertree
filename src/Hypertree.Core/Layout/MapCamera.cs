namespace Hypertree.Layout;

/// <summary>
/// The shared map camera: a world→screen <b>offset</b> per axis (<c>screen = world + offset</c>) that keeps
/// the selection in view without moving unless it must. Navigating moves the cursor over a stationary map;
/// the camera pans — by the minimum needed, leaving a marker and a half of context — only when the selection
/// reaches the edge, and holds still (a dead zone) while it's comfortably on screen. Vertically it also keeps a
/// one-row gutter beyond the first and last rows, so the top/bottom of the stack never pins flush against the
/// viewport edge. When the whole map fits an
/// axis, that axis is centred and pinned, so the cursor moves within a fixed frame. One instance is shared
/// by the interactive map and the transient flash, so the two stay in step. See docs/design/scene-camera.md.
/// </summary>
public sealed class MapCamera
{
    // How much context to keep beyond the selection before the map follows it, in markers (a cell stride
    // horizontally, a row pitch vertically). Capped per axis to the room around the selection so the dead
    // zone is always satisfiable — see Axis.
    private const double EdgeMarginMarkers = 1.5;

    // A gutter kept beyond the first and last rows so the top/bottom of the stack never pins flush against the
    // viewport edge — one row pitch of breathing room around the selection cursor. Vertical only; horizontally
    // the content still pins to the edge (no gutter), so column 0 stays anchored.
    private const double EdgeGutterMarkers = 1.0;

    private double _offX, _offY;
    private bool _framed;

    public double OffsetX => _offX;
    public double OffsetY => _offY;

    /// <summary>Frame the selection centred on the next <see cref="Update"/>, ignoring the carried offset.
    /// Used when the map is (re)opened and on a theme switch, where the old pixel offset no longer matches
    /// the new theme's metrics.</summary>
    public void Reframe() => _framed = false;

    /// <summary>Recompute both offsets for the current selection and viewport. Idempotent while the
    /// selection stays on screen (the dead zone), so calling it every render doesn't drift.</summary>
    public void Update(ICameraLayout layout, double viewW, double viewH)
    {
        LayoutRect sel = layout.SelectionRect;
        (double xLo, double xHi) = layout.WorldX();
        (double yLo, double yHi) = layout.WorldY();

        _offX = Axis(_offX, _framed, sel.Left, sel.Right, xLo, xHi, viewW, layout.Metrics.CellStride * EdgeMarginMarkers);
        _offY = Axis(_offY, _framed, sel.Top, sel.Bottom, yLo, yHi, viewH, layout.Metrics.RowPitch * EdgeMarginMarkers,
                     layout.Metrics.RowPitch * EdgeGutterMarkers);
        _framed = true;
    }

    /// <summary>
    /// The per-axis camera maths, pure so it can be reasoned about and tested in isolation. Returns the new
    /// offset given the current one, the selection's span <c>[selLo, selHi]</c>, the content span
    /// <c>[contentLo, contentHi]</c>, the viewport length <paramref name="view"/>, and the follow
    /// <paramref name="margin"/> (capped here to the room around the selection). <paramref name="framed"/> is
    /// false only for the first framing after a <see cref="Reframe"/>. <paramref name="edgePad"/> lets the
    /// content sit that far beyond each viewport edge, so the first and last markers keep a gutter instead of
    /// pinning flush; zero (the default) restores the strict "no blank gutter" behaviour.
    /// </summary>
    public static double Axis(double offset, bool framed, double selLo, double selHi,
                              double contentLo, double contentHi, double view, double margin, double edgePad = 0)
    {
        double contentSpan = contentHi - contentLo;

        // Fits: centre the whole content and pin it. Independent of the cursor, so moving the selection
        // walks it across a stationary, centred map — never a pan. (Already gutter'd by the centring, so
        // edgePad is only relevant to the overflow case below.)
        if (contentSpan <= view)
            return (view - contentSpan) / 2 - contentLo;

        // Cap the follow margin to the room around the selection. A margin wider than half the free space
        // would make "keep the selection in view with this much margin either side" impossible, and the
        // camera would chase the cursor every step instead of holding still; capping degrades it gracefully
        // to centring the selection when the viewport is that tight.
        double selSpan = selHi - selLo;
        margin = Math.Min(margin, Math.Max(0, (view - selSpan) / 2));

        // The offset range that keeps the content covering the viewport. edgePad opens a deliberate gutter of
        // that size beyond each end, so the first/last markers aren't flush against the edge; with edgePad 0
        // the content pins with no blank gutter at either end. contentSpan > view, so minOffset < maxOffset.
        double minOffset = view - contentHi - edgePad; // content's far edge (plus a gutter) at the viewport's far side
        double maxOffset = -contentLo + edgePad;        // content's near edge (plus a gutter) at the viewport's near side

        double result;
        if (!framed)
        {
            // First framing: centre the selection (then clamp into range below).
            result = view / 2 - (selLo + selHi) / 2;
        }
        else
        {
            double visLo = -offset, visHi = -offset + view;
            double wantLo = selLo - margin, wantHi = selHi + margin;
            if (wantLo < visLo) result = -wantLo;             // selection reached the low edge: pan to it
            else if (wantHi > visHi) result = view - wantHi;  // reached the high edge: pan to it
            else result = offset;                             // comfortably inside: the dead zone — hold
        }

        return Math.Clamp(result, minOffset, maxOffset);
    }
}
