using Hypertree.Scopes;

namespace Hypertree.Layout;

/// <summary>Whether a scene row is the main timeline or a branch.</summary>
public enum RowKind { Main, Branch }

/// <summary>One desktop in the normalised scene. <see cref="Selected"/> is the blue focus/target cell (the
/// interactive map's selection, or the current desktop on a non-interactive surface); <see cref="Here"/> is
/// the green "you are here" marker where the two are shown apart.</summary>
public sealed record SceneCell(string Label, bool Selected, bool Here, int WindowCount);

/// <summary>One timeline in the normalised scene: the main row or a branch, its cells in order, plus the
/// draw hints both themes share (name, colour-by-index handled by the painter, whether it's the active
/// level, and its remembered cursor column).</summary>
public sealed record SceneRow(
    RowKind Kind, int BranchIndex, string Name, bool Active, int Cursor,
    IReadOnlyList<SceneCell> Cells)
{
    public bool IsMain => Kind == RowKind.Main;
}

/// <summary>
/// The theme-agnostic, normalised view of a <see cref="NavMap"/> that both map themes render. Rows are in
/// draw order — branches before <see cref="NavMap.TopPosition"/> above main, main, then the rest below — so
/// a row's list index is its stack position (the same contract the old <c>BoardLayout.Rows</c> carried).
/// The ordering lives here <em>once</em>, rather than being re-derived in each renderer.
/// </summary>
public sealed record Scene(IReadOnlyList<SceneRow> Rows, int SelectionRow, int SelectionCol)
{
    /// <summary>Build the scene from a render-ready map. The selection is the cell marked
    /// <see cref="NavMapTile.IsCurrent"/> (blue) — main's when <see cref="NavMap.OnTop"/>, else the current
    /// branch's; it falls back to main's cursor if nothing is marked.</summary>
    public static Scene From(NavMap map)
    {
        int split = Math.Clamp(map.TopPosition, 0, map.Branches.Count);
        var rows = new List<SceneRow>(map.Branches.Count + 1);

        for (int gi = 0; gi < split; gi++) rows.Add(BranchRow(map.Branches[gi]));
        int mainRow = rows.Count;
        rows.Add(MainRow(map));
        for (int gi = split; gi < map.Branches.Count; gi++) rows.Add(BranchRow(map.Branches[gi]));

        // The selection is whichever cell carries IsCurrent. There should be exactly one; if a surface
        // marks none, fall back to main's remembered cursor so the camera still has something to frame.
        int selRow = mainRow, selCol = Math.Clamp(map.TopCursor, 0, Math.Max(0, map.TopRow.Count - 1));
        for (int r = 0; r < rows.Count; r++)
        {
            IReadOnlyList<SceneCell> cells = rows[r].Cells;
            for (int c = 0; c < cells.Count; c++)
                if (cells[c].Selected) { selRow = r; selCol = c; }
        }
        return new Scene(rows, selRow, selCol);
    }

    private static SceneRow MainRow(NavMap map) => new(
        RowKind.Main, -1, "main", map.OnTop, map.TopCursor,
        map.TopRow.Select(t => new SceneCell(t.Label, map.OnTop && t.IsCurrent, t.IsHere, t.WindowCount)).ToList());

    private static SceneRow BranchRow(NavMapBranch g) => new(
        RowKind.Branch, g.Index, g.Name, g.IsCurrentLevel, g.Cursor,
        g.Desktops.Select(d => new SceneCell(d.Label, d.IsCurrent, d.IsHere, d.WindowCount)).ToList());
}
