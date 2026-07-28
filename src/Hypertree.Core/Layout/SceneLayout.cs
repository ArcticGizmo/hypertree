namespace Hypertree.Layout;

/// <summary>
/// Where every row and cell sits in <b>world space</b> — cursor-independent, so moving the selection does
/// not move the map (that's the camera's job, and only when it must). Rows align at their first desktop:
/// column 0 of every row shares the world column <c>x = 0</c>, and cell <c>c</c> sits at <c>x = c · CellStride</c>.
/// Rows stack on a uniform pitch: row <c>r</c> is centred at <c>y = r · RowPitch</c>. See
/// docs/design/scene-camera.md.
/// </summary>
public sealed class SceneLayout
{
    private readonly SceneMetrics _m;
    private readonly LayoutRect[][] _cells; // [row][col] → world rect
    private readonly LayoutRect[] _rows;    // per-row band → world rect

    public Scene Scene { get; }
    public SceneMetrics Metrics => _m;

    public SceneLayout(Scene scene, SceneMetrics metrics)
    {
        Scene = scene;
        _m = metrics;
        _rows = new LayoutRect[scene.Rows.Count];
        _cells = new LayoutRect[scene.Rows.Count][];

        for (int r = 0; r < scene.Rows.Count; r++)
        {
            IReadOnlyList<SceneCell> cells = scene.Rows[r].Cells;
            double cy = r * _m.RowPitch;                       // row centre Y
            _cells[r] = new LayoutRect[cells.Count];
            for (int c = 0; c < cells.Count; c++)
            {
                double centreX = c * _m.CellStride;            // cell centre X (col 0 → x = 0)
                _cells[r][c] = new LayoutRect(centreX - _m.CellWidth / 2, cy - _m.CellHeight / 2,
                                              _m.CellWidth, _m.CellHeight);
            }
            _rows[r] = RowBand(cells.Count, cy);
        }
    }

    // A row's band spans from the left edge of column 0 to the right edge of its last column, at the row's
    // full height — the strip a drag hit-tests against and the vertical extent the camera frames.
    private LayoutRect RowBand(int cellCount, double cy)
    {
        double rightCentre = Math.Max(0, cellCount - 1) * _m.CellStride;
        double x = -_m.CellWidth / 2;                          // left edge of column 0
        double w = rightCentre + _m.CellWidth;                 // out to the right edge of the last column
        return new LayoutRect(x, cy - _m.RowHeight / 2, w, _m.RowHeight);
    }

    public int RowCount => _rows.Length;
    public LayoutRect CellRect(int row, int col) => _cells[row][col];
    public LayoutRect RowRect(int row) => _rows[row];

    /// <summary>The selection's world rect — the single thing the camera follows.</summary>
    public LayoutRect SelectionRect
    {
        get
        {
            int r = Math.Clamp(Scene.SelectionRow, 0, _cells.Length - 1);
            LayoutRect[] row = _cells[r];
            if (row.Length == 0) return _rows[r];               // an empty row: frame the band itself
            return row[Math.Clamp(Scene.SelectionCol, 0, row.Length - 1)];
        }
    }

    /// <summary>The horizontal world span across every cell — the camera's fits-in-viewport test on X.</summary>
    public (double Lo, double Hi) WorldX()
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (LayoutRect[] row in _cells)
            foreach (LayoutRect cell in row) { lo = Math.Min(lo, cell.Left); hi = Math.Max(hi, cell.Right); }
        return lo <= hi ? (lo, hi) : (0, 0);
    }

    /// <summary>The vertical world span across every row — the camera's fits-in-viewport test on Y.</summary>
    public (double Lo, double Hi) WorldY()
    {
        if (_rows.Length == 0) return (0, 0);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (LayoutRect row in _rows) { lo = Math.Min(lo, row.Top); hi = Math.Max(hi, row.Bottom); }
        return (lo, hi);
    }
}
