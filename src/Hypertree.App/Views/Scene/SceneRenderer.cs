using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hypertree.Layout;
using Hypertree.Scopes;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// The shared driver behind every map theme. It runs the Core pipeline — normalise the <see cref="NavMap"/>
/// into a <see cref="Layout.Scene"/>, lay it out in world space (<see cref="SceneLayout"/>), and pan the
/// <see cref="MapCamera"/> — then turns world rects into screen rects and hands each row to the theme's
/// <see cref="IScenePainter"/> to draw. Because the ordering, first-desktop alignment and dead-zone camera
/// all live here, the board and the metro diagram move identically; only the pixels differ.
///
/// It also emits the same <see cref="BoardLayout"/> the old renderers did (in screen coordinates), so the
/// interactive map's click and drag hit-testing is unchanged. See docs/design/scene-camera.md.
/// </summary>
internal static class SceneRenderer
{
    public static Control Render(
        IScenePainter painter, NavMap map, double screenW, double screenH, double s, MapCamera camera,
        Action<int>? onTopClick = null, Action<int, int>? onBranchClick = null,
        Action<int>? onTopDelete = null, Action<int, int>? onBranchDelete = null,
        Action<int>? onTopActivate = null, Action<int, int>? onBranchActivate = null,
        BoardLayout? layout = null)
    {
        var scene = Layout.Scene.From(map);
        SceneMetrics metrics = painter.Metrics(s);
        var world = new SceneLayout(scene, metrics);

        camera.Update(world, screenW, screenH);
        double ox = camera.OffsetX, oy = camera.OffsetY;

        var canvas = new Canvas { Width = screenW, Height = screenH, ClipToBounds = true, Background = Brushes.Transparent };

        // The spine passes through each row's column-0 centre, in screen space.
        var col0 = new List<(double X, double Y)>(scene.Rows.Count);
        for (int r = 0; r < scene.Rows.Count; r++)
        {
            LayoutRect band = world.RowRect(r);
            double x0 = (scene.Rows[r].Cells.Count > 0 ? world.CellRect(r, 0).CenterX : band.CenterX) + ox;
            col0.Add((x0, band.CenterY + oy));
        }
        painter.PaintSpine(canvas, col0, s);

        for (int r = 0; r < scene.Rows.Count; r++)
        {
            SceneRow row = scene.Rows[r];
            int cellCount = row.Cells.Count;

            var cells = new List<Rect>(cellCount);
            for (int c = 0; c < cellCount; c++) cells.Add(ToRect(world.CellRect(r, c).Offset(ox, oy)));
            Rect bandRect = ToRect(world.RowRect(r).Offset(ox, oy));

            var frame = new RowFrame(row, cells, bandRect, world.RowRect(r).CenterY + oy, col0[r].X);

            (Action<int>? click, Action<int>? activate, Action<int>? del) = CallbacksFor(
                row, onTopClick, onBranchClick, onTopActivate, onBranchActivate, onTopDelete, onBranchDelete);
            painter.PaintRow(canvas, frame, s, click, activate, del);

            Report(layout, row, cells, bandRect, metrics, painter.RowTrailing(row, s));
        }

        return canvas;
    }

    private static (Action<int>?, Action<int>?, Action<int>?) CallbacksFor(
        SceneRow row,
        Action<int>? onTopClick, Action<int, int>? onBranchClick,
        Action<int>? onTopActivate, Action<int, int>? onBranchActivate,
        Action<int>? onTopDelete, Action<int, int>? onBranchDelete)
    {
        if (row.IsMain) return (onTopClick, onTopActivate, onTopDelete);
        int b = row.BranchIndex;
        return (
            onBranchClick is null ? null : c => onBranchClick(b, c),
            onBranchActivate is null ? null : c => onBranchActivate(b, c),
            onBranchDelete is null ? null : c => onBranchDelete(b, c));
    }

    // Fill the hit-test geometry in the same scheme the old renderers used, from the screen rects: the row
    // band, the tile strip (first-tile left, per-cell stride, the inter-cell gap), and a rect per cell.
    private static void Report(BoardLayout? layout, SceneRow row, IReadOnlyList<Rect> cells, Rect band, SceneMetrics m, double trailing)
    {
        if (layout is null) return;
        double firstLeft = cells.Count > 0 ? cells[0].X : band.X;
        double tileTop = cells.Count > 0 ? cells[0].Y : band.Y;
        double gap = m.CellStride - m.CellWidth;
        if (trailing > 0) band = band.WithWidth(band.Width + trailing); // reserve the trailing decoration (route badge)
        layout.Add(new BoardRow(band, row.IsMain, row.BranchIndex, cells.Count,
                                FirstTileLeft: firstLeft, TileStride: m.CellStride, TileGap: gap,
                                TileTop: tileTop, TileHeight: m.CellHeight));
        for (int c = 0; c < cells.Count; c++)
            layout.Add(new BoardTile(cells[c], row.IsMain, row.BranchIndex, c));
    }

    private static Rect ToRect(LayoutRect r) => new(r.X, r.Y, r.Width, r.Height);
}
