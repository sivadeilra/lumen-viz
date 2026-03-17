// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of graph coarsening for multilevel
// partitioning, derived from the published algorithms in:
//
//   [1] T. A. Davis, W. W. Hager, S. P. Kolodziej, S. N. Yeralan,
//       "Algorithm 1003: Mongoose, A Graph Coarsening and Partitioning
//       Library," ACM Trans. Math. Softw. 46(1), Article 7, March 2020.
//       https://doi.org/10.1145/3337792
//
// This code was written from the mathematical descriptions in [1] and
// does NOT derive from the Mongoose source code (GPL-3.0). This is an
// independent implementation.
// ─────────────────────────────────────────────────────────────────────

namespace LumenGraph.Partition;

/// <summary>
/// One level of the coarsening hierarchy for partitioning.
/// Stores the coarsened graph, the fine-to-coarse mapping, and the
/// vertex weights (number of fine vertices each coarse vertex represents).
/// </summary>
public sealed class PartitionCoarseLevel
{
    /// <summary>The coarsened graph.</summary>
    public required GraphModel Graph { get; init; }

    /// <summary>
    /// Maps fine vertex index → coarse vertex index.
    /// Length = fine graph's node count.
    /// </summary>
    public required int[] FineToCoarse { get; init; }

    /// <summary>
    /// Weight of each coarse vertex = number of fine vertices it contains.
    /// Used for balance constraints during partitioning.
    /// Length = coarse graph's node count.
    /// </summary>
    public required double[] VertexWeight { get; init; }
}

/// <summary>
/// Graph coarsening: given a matching, construct the coarsened graph.
///
/// Matched pairs (u,v) merge into a single coarse vertex. Unmatched
/// vertices become singleton coarse vertices. Edge weights between
/// coarse vertices are the sum of all fine edge weights connecting
/// them. Self-loops (edges within a coarse vertex) are discarded.
///
/// Vertex weights accumulate: if coarse vertex c represents fine
/// vertices with total weight W, then vertexWeight[c] = W. This
/// preserves balance information across multiple coarsening levels.
///
/// From [1] Section 2: "The weight of a coarsened edge (ci, cj) is the
/// sum of the weights of the original edges (ui, uj) where ui maps to
/// ci and uj maps to cj."
/// </summary>
public static class GraphCoarsening
{
    /// <summary>
    /// Build the full coarsening hierarchy from the original graph.
    /// Returns a list of levels from finest coarsening to coarsest.
    /// </summary>
    public static List<PartitionCoarseLevel> BuildHierarchy(
        GraphModel original, PartitionOptions options)
    {
        var levels = new List<PartitionCoarseLevel>();
        var current = original;
        // Initial vertex weights: each vertex represents itself
        var currentWeights = new double[original.NodeCount];
        Array.Fill(currentWeights, 1.0);

        for (int lvl = 0; lvl < options.MaxLevels; lvl++)
        {
            if (current.NodeCount <= options.CoarsenLimit)
                break;

            var match = Matching.ComputeMatching(current, options.Matching, options);
            var level = Coarsen(current, match, currentWeights);

            // Stop if coarsening stalled (< 15% reduction)
            if (level.Graph.NodeCount > current.NodeCount * 0.85)
                break;

            levels.Add(level);
            current = level.Graph;
            currentWeights = level.VertexWeight;
        }

        return levels;
    }

    /// <summary>
    /// Construct a coarsened graph from a matching.
    /// </summary>
    public static PartitionCoarseLevel Coarsen(
        GraphModel graph, int[] match, double[] vertexWeight)
    {
        int n = graph.NodeCount;

        // Phase 1: Assign coarse vertex IDs.
        // Matched pairs share a coarse ID; singletons get their own.
        var fineToCoarse = new int[n];
        Array.Fill(fineToCoarse, -1);
        int coarseCount = 0;

        for (int u = 0; u < n; u++)
        {
            if (fineToCoarse[u] >= 0) continue; // already assigned

            fineToCoarse[u] = coarseCount;
            int partner = match[u];
            if (partner >= 0 && partner != u)
            {
                fineToCoarse[partner] = coarseCount;
            }
            coarseCount++;
        }

        // Phase 2: Compute coarse vertex weights
        var coarseWeight = new double[coarseCount];
        for (int u = 0; u < n; u++)
        {
            coarseWeight[fineToCoarse[u]] += vertexWeight[u];
        }

        // Phase 3: Build coarse edges with weight accumulation.
        // Use a dictionary keyed by canonical (min,max) pair.
        // For very large graphs, this could be replaced with a hash
        // table over the adjacency to avoid Dictionary overhead.
        var edgeMap = new Dictionary<long, double>();

        for (int e = 0; e < graph.EdgeCount; e++)
        {
            int cu = fineToCoarse[graph.EdgeSource[e]];
            int cv = fineToCoarse[graph.EdgeTarget[e]];
            if (cu == cv) continue; // internal edge, discard

            long key = cu < cv
                ? ((long)cu << 32) | (uint)cv
                : ((long)cv << 32) | (uint)cu;

            if (edgeMap.TryGetValue(key, out double existing))
                edgeMap[key] = existing + graph.EdgeWeight[e];
            else
                edgeMap[key] = graph.EdgeWeight[e];
        }

        // Phase 4: Build the coarse GraphModel
        var coarse = new GraphModel { Title = graph.Title + " (coarsened)" };
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

        return new PartitionCoarseLevel
        {
            Graph = coarse,
            FineToCoarse = fineToCoarse,
            VertexWeight = coarseWeight,
        };
    }
}
