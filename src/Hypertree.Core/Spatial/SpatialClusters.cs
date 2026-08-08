namespace Hypertree.Spatial;

/// <summary>
/// Splits a set of grid positions into <b>contiguous fragments</b> — maximal groups of cells that touch,
/// counting the eight neighbours (orthogonal <em>and</em> diagonal, i.e. Chebyshev distance 1). This is what
/// decides how many hulls a group draws (one per fragment), so a group whose rooms have drifted apart reads
/// as visibly broken; and it's the unit Tidy reunites, moving each fragment as a rigid block to preserve the
/// little arrangements inside it. Pure and index-based so it's trivially testable and reused by both the
/// renderer and Tidy.
/// </summary>
public static class SpatialClusters
{
    /// <summary>Partition <paramref name="positions"/> into fragments, each returned as the list of input
    /// indices it contains. Fragments come out in ascending order of their smallest member index, so the
    /// result is deterministic. An empty input yields no fragments.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Fragments(IReadOnlyList<GridPos> positions)
    {
        int n = positions.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int a) => parent[a] == a ? a : (parent[a] = Find(parent[a]));
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Math.Abs(positions[i].X - positions[j].X) <= 1 &&
                    Math.Abs(positions[i].Y - positions[j].Y) <= 1)
                    Union(i, j);

        // Group indices by their representative, keeping the first-seen order of each fragment's root so the
        // output is stable across runs.
        var order = new List<int>();
        var byRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<int>? bucket))
            {
                bucket = new List<int>();
                byRoot[root] = bucket;
                order.Add(root);
            }
            bucket.Add(i);
        }
        return order.Select(root => (IReadOnlyList<int>)byRoot[root]).ToList();
    }
}
