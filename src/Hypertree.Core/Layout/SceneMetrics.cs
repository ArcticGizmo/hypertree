namespace Hypertree.Layout;

/// <summary>
/// The theme-supplied sizing the shared layout and camera need — plain numbers, so the geometry stays pure
/// and testable. A painter fills this in for its own look (tiles vs. stations); the layout algorithm treats
/// every theme the same. All values are already scaled by the render scale <c>s</c>.
/// </summary>
/// <param name="CellStride">Distance between adjacent cell <em>centres</em> along a row.</param>
/// <param name="CellWidth">A cell's drawable width — used for hit-testing and framing the selection.</param>
/// <param name="CellHeight">A cell's drawable height.</param>
/// <param name="RowPitch">Distance between adjacent row <em>centres</em> (uniform across the stack).</param>
/// <param name="RowHeight">A row band's height — used for hit-testing and vertical framing.</param>
public sealed record SceneMetrics(
    double CellStride,
    double CellWidth,
    double CellHeight,
    double RowPitch,
    double RowHeight);
