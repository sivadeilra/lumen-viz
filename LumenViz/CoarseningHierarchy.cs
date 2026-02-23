namespace LumenViz;

/// <summary>
/// Holds the full HEM coarsening hierarchy for a graph, including
/// the original graph and all coarsened levels. Each level has its
/// own laid-out GraphModel and the fine→coarse mapping that connects
/// it to the next coarser level.
///
/// Level 0 = original graph (finest).
/// Level N = coarsest graph.
///
/// Used for multi-level visualization: the user can navigate up/down
/// through coarsening levels, and the renderer can highlight parent-
/// child relationships between adjacent levels.
/// </summary>
public sealed class CoarseningHierarchy
{
    /// <summary>
    /// All graphs in order: [0] = original (finest), [^1] = coarsest.
    /// </summary>
    public GraphModel[] Graphs { get; }

    /// <summary>
    /// FineToCoarse mappings. FineToCoarse[i] maps nodes at level i
    /// to node indices at level i+1. Length = Graphs.Length - 1.
    /// FineToCoarse[i][nodeAtLevelI] = nodeAtLevelI+1.
    /// </summary>
    public int[][] FineToCoarse { get; }

    /// <summary>Number of levels (including original).</summary>
    public int LevelCount => Graphs.Length;

    public CoarseningHierarchy(GraphModel original, List<CoarseLevel> levels)
    {
        // Graphs: [original, level0.Graph, level1.Graph, ...]
        Graphs = new GraphModel[levels.Count + 1];
        Graphs[0] = original;
        for (int i = 0; i < levels.Count; i++)
            Graphs[i + 1] = levels[i].Graph;

        // FineToCoarse: maps[i] connects Graphs[i] → Graphs[i+1]
        FineToCoarse = new int[levels.Count][];
        for (int i = 0; i < levels.Count; i++)
            FineToCoarse[i] = levels[i].FineToCoarse;
    }

    /// <summary>
    /// Map a node index at a fine level to the corresponding super-node
    /// at a coarser level. Walks up through intermediate mappings.
    /// </summary>
    public int MapUp(int nodeIndex, int fromLevel, int toLevel)
    {
        int idx = nodeIndex;
        for (int lvl = fromLevel; lvl < toLevel; lvl++)
            idx = FineToCoarse[lvl][idx];
        return idx;
    }

    /// <summary>
    /// Get all fine-level (fromLevel) node indices that map to a given
    /// coarse node at toLevel. Walks down through intermediate mappings.
    /// Returns a list (small allocation — called rarely for UI highlighting).
    /// </summary>
    public List<int> MapDown(int coarseNodeIndex, int fromLevel, int toLevel)
    {
        // Start with the single coarse node
        var current = new List<int> { coarseNodeIndex };

        // Walk down from fromLevel toward toLevel (finer)
        for (int lvl = fromLevel - 1; lvl >= toLevel; lvl--)
        {
            var next = new List<int>();
            var map = FineToCoarse[lvl];
            int fineCount = Graphs[lvl].NodeCount;
            foreach (int coarseIdx in current)
            {
                for (int fi = 0; fi < fineCount; fi++)
                {
                    if (map[fi] == coarseIdx)
                        next.Add(fi);
                }
            }
            current = next;
        }
        return current;
    }
}
