using Avalonia;
using Avalonia.Controls;
using Hypertree.Layout;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The screen geometry of one row, handed to a painter so it only has to draw — the shared driver has
/// already run the scene layout and applied the camera, so these rects are in final canvas coordinates.
/// </summary>
/// <param name="Row">The row's normalised content (cells, name, active flag, kind).</param>
/// <param name="Cells">Each cell's screen rect, indexed by column.</param>
/// <param name="Band">The row's band (column 0's left edge out to the last column's right edge).</param>
/// <param name="CentreY">The row's centre line in screen space — where a cell's midline sits.</param>
/// <param name="Col0X">The screen X of column 0's centre — where the spine passes through this row.</param>
internal sealed record RowFrame(
    SceneRow Row, IReadOnlyList<Rect> Cells, Rect Band, double CentreY, double Col0X);

/// <summary>
/// A map theme — the board or the metro diagram — reduced to <em>drawing</em>. It supplies the sizing the
/// shared layout needs (<see cref="Metrics"/>) and knows how to paint one row's cells and the spine at the
/// screen positions the driver computed; it owns none of the ordering, alignment, or camera logic (that's
/// <see cref="SceneRenderer"/> + Core's <see cref="SceneLayout"/>/<see cref="MapCamera"/>), so both themes
/// move identically. Click/activate/delete handlers are wired per cell, mirroring the old renderers.
/// </summary>
internal interface IScenePainter
{
    /// <summary>The theme's sizing at render scale <paramref name="s"/>, for the shared world layout.</summary>
    SceneMetrics Metrics(double s);

    /// <summary>Extra width to append to a row's reported hit band beyond its last cell, reserving a
    /// theme-specific trailing decoration as part of the row's drag handle — the metro route badge. Zero for
    /// themes (the board) whose row is bounded by its cells. Purely a hit-testing concern; the painter still
    /// draws the decoration itself.</summary>
    double RowTrailing(SceneRow row, double s);

    /// <summary>Draw one row: its background decor (branch box / route + badge) and each cell (tile /
    /// station + chip), at the screen rects in <paramref name="frame"/>. The callbacks are per column and
    /// null when not interactive; <paramref name="onDelete"/> is null for themes without a delete affordance.</summary>
    void PaintRow(Canvas canvas, RowFrame frame, double s,
                  Action<int>? onClick, Action<int>? onActivate, Action<int>? onDelete);

    /// <summary>Draw the spine/connectors joining consecutive rows through the shared column-0 world column,
    /// at the given screen centres (one per row, top to bottom).</summary>
    void PaintSpine(Canvas canvas, IReadOnlyList<(double X, double Y)> col0Centres, double s);
}
