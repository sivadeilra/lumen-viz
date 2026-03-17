// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of multilevel graph bisection, derived
// from the published algorithms in:
//
//   [1] T. A. Davis, W. W. Hager, S. P. Kolodziej, S. N. Yeralan,
//       "Algorithm 1003: Mongoose, A Graph Coarsening and Partitioning
//       Library," ACM Trans. Math. Softw. 46(1), Article 7, March 2020.
//       https://doi.org/10.1145/3337792
//
//   [2] T. A. Davis, W. W. Hager, J. T. Hungerford,
//       "An Efficient Hybrid Algorithm for the Separable Convex
//       Quadratic Knapsack Problem," ACM Trans. Math. Softw. 42(3),
//       Article 19, June 2016.
//       https://doi.org/10.1145/2828635
//
//   [3] T. A. Davis, W. W. Hager,
//       "Dual Multilevel Optimization," Math. Programming 112(2),
//       pp. 403–425, April 2008.
//       https://doi.org/10.1007/s10107-006-0022-3
//
// This code was written from the mathematical descriptions in the above
// papers and does NOT derive from the Mongoose source code (GPL-3.0).
// This is an independent implementation.
//
// ─── ALGORITHM OVERVIEW ─────────────────────────────────────────────
//
// Multilevel graph bisection proceeds in three phases:
//
// Phase 1 — COARSEN:
//   Repeatedly compute a maximal matching (HEM/HEMSR/HEMSRdeg) and
//   merge matched vertex pairs into super-nodes, building a hierarchy
//   of progressively smaller graphs. Edge weights between super-nodes
//   are summed. Stops when the graph is small enough for direct
//   partitioning (default: 64 vertices) or coarsening stalls.
//   See [1] Section 2.
//
// Phase 2 — INITIAL PARTITION:
//   Compute an initial bisection of the coarsest graph using multiple
//   strategies (BFS, random, greedy) and keep the best. The coarsest
//   graph is small, so these are cheap. See [1] Section 3.
//
// Phase 3 — UNCOARSEN + REFINE:
//   Project the partition from each coarser level to the next finer
//   level. At each level, refine using:
//     a) QP relaxation: relax to continuous [0,1], gradient projection
//        with napsack constraint projection, boundary pushes, then
//        round back to discrete. See [1] Section 3, [2], [3].
//     b) FM local search: greedily move vertices across the partition
//        boundary to reduce edge cut. See [1] Section 3.
//   Continue until the original (finest) graph is reached.
//
// The result is a balanced bisection with low edge cut.
//
// For recursive k-way partitioning, call Bisect() recursively on
// each half. This gives O(log k) levels of recursion.
// ─────────────────────────────────────────────────────────────────────

namespace LumenGraph.Partition;

/// <summary>
/// Multilevel graph bisection following the Mongoose framework [1].
///
/// Usage:
///   var result = EdgeSeparator.Bisect(graph);
///   // result.Side[i] = false → partition A, true → partition B
///   // result.EdgeCut = total weight of edges crossing the partition
///
/// For k-way partitioning:
///   var parts = EdgeSeparator.RecursiveBisect(graph, k);
///   // parts[i] = partition ID (0..k-1) for vertex i
/// </summary>
public static class EdgeSeparator
{
    /// <summary>
    /// Compute a balanced bisection of the graph using multilevel
    /// coarsening, initial partitioning, and QP+FM refinement.
    /// </summary>
    public static PartitionResult Bisect(GraphModel graph, PartitionOptions? options = null)
    {
        options ??= new PartitionOptions();
        int n = graph.NodeCount;

        if (n == 0) return new PartitionResult(0);
        if (n == 1)
        {
            var trivial = new PartitionResult(1);
            trivial.Recompute(graph);
            return trivial;
        }

        // Unit vertex weights for the original graph
        var vertexWeight = new double[n];
        Array.Fill(vertexWeight, 1.0);

        // Phase 1: Coarsen
        var levels = GraphCoarsening.BuildHierarchy(graph, options);

        // Get the coarsest graph and its vertex weights
        GraphModel coarsest;
        double[] coarsestWeights;
        if (levels.Count > 0)
        {
            coarsest = levels[^1].Graph;
            coarsestWeights = levels[^1].VertexWeight;
        }
        else
        {
            coarsest = graph;
            coarsestWeights = vertexWeight;
        }

        // Phase 2: Initial partition on the coarsest graph
        var partition = InitialPartition.Compute(coarsest, coarsestWeights, options);

        // Phase 3: Uncoarsen + refine
        for (int lvl = levels.Count - 1; lvl >= 0; lvl--)
        {
            var level = levels[lvl];
            var fineGraph = lvl > 0 ? levels[lvl - 1].Graph : graph;
            var fineWeights = lvl > 0 ? levels[lvl - 1].VertexWeight : vertexWeight;

            // Project partition from coarse to fine level
            partition = ProjectPartition(partition, level.FineToCoarse, fineGraph.NodeCount);

            // Refine with QP
            QpRefine.Refine(fineGraph, partition, fineWeights, options);

            // Refine with FM
            FmRefine.Refine(fineGraph, partition, fineWeights, options);
        }

        // If no coarsening happened, still refine the original
        if (levels.Count == 0)
        {
            QpRefine.Refine(graph, partition, vertexWeight, options);
            FmRefine.Refine(graph, partition, vertexWeight, options);
        }

        return partition;
    }

    /// <summary>
    /// Recursive bisection to produce a k-way partition.
    /// Returns an array where result[i] = partition ID (0..k-1) for vertex i.
    ///
    /// Uses a queue-based approach: bisect the whole graph, then bisect
    /// each half, etc., until k parts are reached. At each step, the
    /// largest part is bisected next.
    /// </summary>
    public static int[] RecursiveBisect(
        GraphModel graph, int k, PartitionOptions? options = null)
    {
        options ??= new PartitionOptions();
        int n = graph.NodeCount;

        if (k <= 1 || n == 0)
        {
            return new int[n]; // all in partition 0
        }

        // Start with all vertices in one group
        var partId = new int[n];
        var groups = new List<List<int>> { new List<int>() };
        for (int i = 0; i < n; i++)
            groups[0].Add(i);

        int nextPartId = 1;

        // Priority queue: bisect the largest group first
        // Use a simple approach: find the largest group each iteration
        while (groups.Count < k)
        {
            // Find largest group
            int largestIdx = 0;
            for (int g = 1; g < groups.Count; g++)
            {
                if (groups[g].Count > groups[largestIdx].Count)
                    largestIdx = g;
            }

            var group = groups[largestIdx];
            if (group.Count <= 1) break; // can't split further

            // Build subgraph for this group
            var (subgraph, subToOriginal, originalToSub) =
                BuildSubgraph(graph, group);

            // Bisect the subgraph
            var subResult = Bisect(subgraph, options);

            // Split the group: vertices in side A keep old ID, side B gets new ID
            int newId = nextPartId++;
            var groupA = new List<int>();
            var groupB = new List<int>();

            for (int si = 0; si < subgraph.NodeCount; si++)
            {
                int orig = subToOriginal[si];
                if (subResult.Side[si])
                {
                    partId[orig] = newId;
                    groupB.Add(orig);
                }
                else
                {
                    groupA.Add(orig);
                }
            }

            groups[largestIdx] = groupA;
            groups.Add(groupB);
        }

        return partId;
    }

    /// <summary>
    /// Project a partition from the coarse level to the fine level.
    /// Each fine vertex inherits the partition of its coarse parent.
    /// </summary>
    private static PartitionResult ProjectPartition(
        PartitionResult coarsePartition, int[] fineToCoarse, int fineCount)
    {
        var fine = new PartitionResult(fineCount);
        for (int i = 0; i < fineCount; i++)
        {
            fine.Side[i] = coarsePartition.Side[fineToCoarse[i]];
        }
        return fine;
    }

    /// <summary>
    /// Build an induced subgraph from a subset of vertices.
    /// Returns the subgraph, a mapping from sub-indices to original indices,
    /// and the reverse mapping (original → sub, -1 if not in subset).
    /// </summary>
    private static (GraphModel subgraph, int[] subToOriginal, int[] originalToSub)
        BuildSubgraph(GraphModel graph, List<int> vertices)
    {
        int n = graph.NodeCount;
        int subN = vertices.Count;

        var originalToSub = new int[n];
        Array.Fill(originalToSub, -1);

        var subToOriginal = new int[subN];
        for (int si = 0; si < subN; si++)
        {
            subToOriginal[si] = vertices[si];
            originalToSub[vertices[si]] = si;
        }

        // Collect edges within the subgraph
        var edges = new List<(int src, int tgt, double weight)>();
        var seen = new HashSet<long>();

        for (int si = 0; si < subN; si++)
        {
            int u = vertices[si];
            int start = graph.AdjOffset[u];
            int end = graph.AdjOffset[u + 1];
            for (int ai = start; ai < end; ai++)
            {
                int v = graph.AdjList[ai];
                int sv = originalToSub[v];
                if (sv < 0) continue; // v not in subset

                long key = si < sv
                    ? ((long)si << 32) | (uint)sv
                    : ((long)sv << 32) | (uint)si;

                if (seen.Add(key))
                {
                    edges.Add((si, sv, graph.AdjWeight[ai]));
                }
            }
        }

        var sub = new GraphModel();
        sub.AllocNodes(subN);
        sub.AllocEdges(edges.Count);

        for (int e = 0; e < edges.Count; e++)
        {
            sub.EdgeSource[e] = edges[e].src;
            sub.EdgeTarget[e] = edges[e].tgt;
            sub.EdgeWeight[e] = edges[e].weight;
        }

        sub.BuildAdjacency();

        // Copy labels
        for (int si = 0; si < subN; si++)
            sub.Labels[si] = graph.Labels[subToOriginal[si]];

        return (sub, subToOriginal, originalToSub);
    }
}
