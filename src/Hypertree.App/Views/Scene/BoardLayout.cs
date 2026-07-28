using Avalonia;

namespace Hypertree.App.Views;

/// <summary>
/// Where a rendered map put its cells and rows, in canvas (screen) coordinates. <see cref="Scene.SceneRenderer"/>
/// fills one when asked so the interactive map can turn a pointer position into a drag source or a drop slot
/// without re-deriving the layout maths. <see cref="Rows"/> is in draw order (top to bottom), which is
/// exactly the map's combined row sequence — branches above main, main, then the branches below — so a
/// row's list position is its row index. (Theme-agnostic: both painters report through the same driver.)
/// </summary>
internal sealed class BoardLayout
{
    private readonly List<BoardTile> _tiles = new();
    private readonly List<BoardRow> _rows = new();

    public IReadOnlyList<BoardTile> Tiles => _tiles;
    public IReadOnlyList<BoardRow> Rows => _rows;

    public void Add(BoardTile tile) => _tiles.Add(tile);
    public void Add(BoardRow row) => _rows.Add(row);

    // ── Row boundaries (where a dragged branch slots in) ───────────────────────────
    // The vertical counterpart of a row's tile boundaries: 0 is above the first row, <see cref="Rows"/>.Count
    // is below the last, and boundary i splits rows i-1 and i. A dragged branch inserts at one of these, so
    // the separator the map draws and the slot the drop resolves to come from the same geometry.

    /// <summary>The number of insertion boundaries — one more than there are rows.</summary>
    public int BoundaryCount => _rows.Count + 1;

    /// <summary>The y a separator for <paramref name="boundary"/> is drawn at: the middle of the gap between
    /// the two rows it splits, or just clear of the stack at either end.</summary>
    public double BoundaryY(int boundary)
    {
        if (_rows.Count == 0) return 0;
        if (boundary <= 0) return _rows[0].Bounds.Top - EndOffset;
        if (boundary >= _rows.Count) return _rows[^1].Bounds.Bottom + EndOffset;
        return (_rows[boundary - 1].Bounds.Bottom + _rows[boundary].Bounds.Top) / 2;
    }

    /// <summary>How wide to draw that separator: across both rows it splits, so it reads as belonging to the
    /// gap between them.</summary>
    public (double Left, double Right) BoundarySpan(int boundary)
    {
        if (_rows.Count == 0) return (0, 0);
        Rect above = _rows[Math.Clamp(boundary - 1, 0, _rows.Count - 1)].Bounds;
        Rect below = _rows[Math.Clamp(boundary, 0, _rows.Count - 1)].Bounds;
        return (Math.Min(above.Left, below.Left), Math.Max(above.Right, below.Right));
    }

    /// <summary>The boundary a drop at <paramref name="y"/> asks for — the nearest one, so a drop anywhere
    /// in the stack resolves.</summary>
    public int NearestBoundary(double y)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int b = 0; b < BoundaryCount; b++)
        {
            double d = Math.Abs(y - BoundaryY(b));
            if (d < bestDistance) { best = b; bestDistance = d; }
        }
        return best;
    }

    // How far clear of the top/bottom row the two end separators sit. A visual nudge, not the real row gap —
    // it only has to read as "outside the stack".
    private const double EndOffset = 12;
}

/// <summary>One tile's rectangle, tagged with the slot it draws (main-timeline index, or branch + index).</summary>
internal sealed record BoardTile(Rect Bounds, bool OnMain, int BranchIndex, int DesktopIndex);

/// <summary>
/// One row's band on the map — the whole main row, or a branch's box/line — plus the tile-strip geometry a
/// drop needs: the left edge of the first tile, the per-tile stride, and the strip's vertical extent.
/// </summary>
internal sealed record BoardRow(Rect Bounds, bool IsMain, int BranchIndex, int DesktopCount,
                               double FirstTileLeft, double TileStride, double TileGap,
                               double TileTop, double TileHeight)
{
    /// <summary>The insertion point (0..<see cref="DesktopCount"/>) a drop at <paramref name="x"/> asks
    /// for — the boundary nearest the pointer, so the left half of a tile inserts before it.</summary>
    public int InsertIndexAt(double x)
        => Math.Clamp((int)Math.Round((x - FirstTileLeft) / TileStride), 0, DesktopCount);

    /// <summary>The x of insertion boundary <paramref name="index"/> — where the drop caret is drawn: the
    /// middle of the gap before that tile (and just off the ends for the first/last boundary).</summary>
    public double BoundaryX(int index)
        => FirstTileLeft + index * TileStride - TileGap / 2;

    /// <summary>How far <paramref name="y"/> is outside this row's band (0 when inside) — used to pick the
    /// row a drop in the gap between rows belongs to.</summary>
    public double VerticalDistanceTo(double y)
        => y < Bounds.Top ? Bounds.Top - y : y > Bounds.Bottom ? y - Bounds.Bottom : 0;
}
