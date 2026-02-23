namespace LumenGraph;

/// <summary>
/// One level of the HEM coarsening hierarchy.
/// Maps fine nodes → coarse super-nodes.
/// </summary>
public sealed class CoarseLevel
{
    /// <summary>The coarsened graph at this level.</summary>
    public required GraphModel Graph { get; init; }

    /// <summary>Maps fine node index → coarse node index (length = fine node count).</summary>
    public required int[] FineToCoarse { get; init; }
}

/// <summary>
/// Heavy-Edge Matching (HEM) graph coarsening, as used in METIS
/// and sfdp (Graphviz). Produces a hierarchy of progressively smaller
/// graphs for multi-level layout.
///
/// Each level maps fine nodes → coarse super-nodes via maximal matching
/// that prefers heavy edges. Edge weights between super-nodes are summed.
/// Coarsening ratio is ~2:1 per level, yielding O(log n) levels.
///
/// Time: O(|E|) per level, O(|E| log n) total.
/// Space: O(|V| + |E|) per level.
/// </summary>
public static class Coarsening
{
    /// <summary>
    /// Build a coarsening hierarchy by repeatedly applying HEM.
    /// Returns the list of levels (index 0 = first coarsened from original,
    /// last = coarsest). The original graph is NOT included in the list.
    /// </summary>
    public static List<CoarseLevel> BuildHierarchy(GraphModel original, int minSize = 50)
    {
        var levels = new List<CoarseLevel>();
        var current = original;

        while (current.NodeCount > minSize)
        {
            var level = HeavyEdgeMatch(current);

            // Stop if coarsening didn't reduce enough (< 20% reduction)
            if (level.Graph.NodeCount > current.NodeCount * 0.8)
                break;

            levels.Add(level);
            current = level.Graph;
        }

        return levels;
    }

    /// <summary>
    /// Perform one round of Heavy-Edge Matching coarsening.
    ///
    /// Visits nodes in random order. For each unmatched node, finds
    /// its heaviest unmatched neighbor (using AdjWeight from CSR) and
    /// merges them into a single coarse super-node. Unmatched singletons
    /// become their own super-nodes.
    ///
    /// Then builds the coarse graph by mapping all edges through the
    /// fine→coarse mapping, merging parallel edges by summing weights.
    /// </summary>
    public static CoarseLevel HeavyEdgeMatch(GraphModel graph)
    {
        int n = graph.NodeCount;
        var matched = new bool[n];
        var fineToCoarse = new int[n];
        int coarseCount = 0;

        // Random permutation — critical for matching quality
        var rng = new Random(42);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        rng.Shuffle(order);

        // Phase 1: Match nodes
        for (int oi = 0; oi < n; oi++)
        {
            int u = order[oi];
            if (matched[u]) continue;

            // Find heaviest unmatched neighbor using CSR adjacency + weights
            int bestV = -1;
            double bestW = -1;

            int adjStart = graph.AdjOffset[u];
            int adjEnd = graph.AdjOffset[u + 1];
            for (int ai = adjStart; ai < adjEnd; ai++)
            {
                int v = graph.AdjList[ai];
                if (matched[v]) continue;
                double w = graph.AdjWeight[ai];
                if (w > bestW)
                {
                    bestW = w;
                    bestV = v;
                }
            }

            // Create coarse node (u alone, or u+bestV merged)
            fineToCoarse[u] = coarseCount;
            matched[u] = true;

            if (bestV >= 0)
            {
                fineToCoarse[bestV] = coarseCount;
                matched[bestV] = true;
            }

            coarseCount++;
        }

        // Phase 2: Build coarse graph
        // Collect coarse edges, merging parallel edges by summing weights.
        // Key encoding: (min, max) packed into a long.
        var edgeMap = new Dictionary<long, double>();

        for (int e = 0; e < graph.EdgeCount; e++)
        {
            int cu = fineToCoarse[graph.EdgeSource[e]];
            int cv = fineToCoarse[graph.EdgeTarget[e]];
            if (cu == cv) continue; // internal edge — skip

            // Canonical key: smaller index first
            long key = cu < cv
                ? ((long)cu << 32) | (uint)cv
                : ((long)cv << 32) | (uint)cu;

            if (edgeMap.TryGetValue(key, out double existing))
                edgeMap[key] = existing + graph.EdgeWeight[e];
            else
                edgeMap[key] = graph.EdgeWeight[e];
        }

        // Build the coarse GraphModel
        var coarse = new GraphModel();
        coarse.AllocNodes(coarseCount);
        coarse.AllocEdges(edgeMap.Count);

        int ei = 0;
        foreach (var (key, w) in edgeMap)
        {
            coarse.EdgeSource[ei] = (int)(key >> 32);
            coarse.EdgeTarget[ei] = (int)(key & 0xFFFFFFFF);
            coarse.EdgeWeight[ei] = w;
            ei++;
        }

        coarse.BuildAdjacency();

        return new CoarseLevel
        {
            Graph = coarse,
            FineToCoarse = fineToCoarse,
        };
    }
}
