namespace Hypertree.Layout;

/// <summary>
/// The shared map camera: a world→screen <b>offset</b> per axis (<c>screen = world + offset</c>) that keeps
/// the selection in view without moving unless it must. Navigating moves the cursor over a stationary map;
/// the camera pans — by the minimum needed, leaving one marker of context — only when the selection reaches
/// the edge, and holds still (a dead zone) while it's comfortably on screen. When the whole map fits an
/// axis, that axis is centred and pinned, so the cursor moves within a fixed frame. One instance is shared
/// by the interactive map and the transient flash, so the two stay in step. See docs/design/scene-camera.md.
/// </summary>
public sealed class MapCamera
{
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
    public void Update(SceneLayout layout, double viewW, double viewH)
    {
        LayoutRect sel = layout.SelectionRect;
        (double xLo, double xHi) = layout.WorldX();
        (double yLo, double yHi) = layout.WorldY();

        _offX = Axis(_offX, _framed, sel.Left, sel.Right, xLo, xHi, viewW, layout.Metrics.CellStride);
        _offY = Axis(_offY, _framed, sel.Top, sel.Bottom, yLo, yHi, viewH, layout.Metrics.RowPitch);
        _framed = true;
    }

    /// <summary>
    /// The per-axis camera maths, pure so it can be reasoned about and tested in isolation. Returns the new
    /// offset given the current one, the selection's span <c>[selLo, selHi]</c>, the content span
    /// <c>[contentLo, contentHi]</c>, the viewport length <paramref name="view"/>, and the follow
    /// <paramref name="margin"/> (one marker). <paramref name="framed"/> is false only for the first framing
    /// after a <see cref="Reframe"/>.
    /// </summary>
    public static double Axis(double offset, bool framed, double selLo, double selHi,
                              double contentLo, double contentHi, double view, double margin)
    {
        double contentSpan = contentHi - contentLo;

        // Fits: centre the whole content and pin it. Independent of the cursor, so moving the selection
        // walks it across a stationary, centred map — never a pan.
        if (contentSpan <= view)
            return (view - contentSpan) / 2 - contentLo;

        // The offset range that keeps the content covering the viewport with no blank gutter at either end.
        // contentSpan > view here, so minOffset < maxOffset.
        double minOffset = view - contentHi; // content's right edge pinned to the viewport's right
        double maxOffset = -contentLo;        // content's left edge pinned to the viewport's left

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
