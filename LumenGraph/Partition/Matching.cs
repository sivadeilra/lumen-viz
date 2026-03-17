// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of matching algorithms for multilevel
// graph partitioning, derived from the published algorithms in:
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
/// Matching algorithms for graph coarsening.
///
/// A matching M is a set of vertex pairs (u,v) such that each vertex
/// appears in at most one pair. Unmatched vertices become singletons.
/// The matching drives coarsening: matched pairs merge into super-nodes.
///
/// Algorithm overview from [1] Section 2:
///
/// HEM (Heavy-Edge Matching):
///   Visit vertices in random order. For each unmatched vertex u, find
///   the unmatched neighbor v with the heaviest edge weight. Match (u,v).
///
/// HEMSR (HEM with Stall Reducing):
///   After HEM, a second pass finds "brotherly" matches: for each still-
///   unmatched vertex u, find its heaviest MATCHED neighbor v. Then among
///   v's unmatched neighbors, find another unmatched vertex w (heaviest
///   edge to v). Match (u,w) — the "brothers" of v.
///
/// HEMSRdeg (degree-gated HEMSR):
///   Like HEMSR, but the stall-reducing pass only considers matched
///   vertices v whose degree exceeds a threshold (degreeThreshold × avgDeg).
///   This avoids wasting time on low-degree vertices where stall-reducing
///   is unlikely to help.
/// </summary>
public static class Matching
{
    private const int Unmatched = -1;

    /// <summary>
    /// Compute a matching of the graph vertices.
    /// Returns match[i] = partner of vertex i, or -1 if unmatched (singleton).
    /// </summary>
    public static int[] ComputeMatching(
        GraphModel graph, MatchingStrategy strategy, PartitionOptions options)
    {
        int n = graph.NodeCount;
        var match = new int[n];
        Array.Fill(match, Unmatched);

        if (n == 0) return match;

        var rng = new Random(options.Seed);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        rng.Shuffle(order);

        if (strategy == MatchingStrategy.Random || options.RandomMatch)
        {
            RandomMatching(graph, match, order);
            return match;
        }

        // Phase 1: Heavy-Edge Matching
        HeavyEdgeMatching(graph, match, order);

        // Phase 2: Stall-reducing (if requested)
        if (strategy == MatchingStrategy.HEMSR)
        {
            StallReducing(graph, match, order, degreeGated: false, degreeThreshold: 0);
        }
        else if (strategy == MatchingStrategy.HEMSRdeg)
        {
            double avgDeg = graph.NodeCount > 0
                ? 2.0 * graph.EdgeCount / graph.NodeCount
                : 0;
            double threshold = options.DegreeThreshold * avgDeg;
            StallReducing(graph, match, order, degreeGated: true, degreeThreshold: threshold);
        }

        return match;
    }

    /// <summary>
    /// Random matching: visit in random order, greedily match with any
    /// unmatched neighbor.
    /// </summary>
    private static void RandomMatching(GraphModel graph, int[] match, int[] order)
    {
        int n = graph.NodeCount;
        for (int oi = 0; oi < n; oi++)
        {
            int u = order[oi];
            if (match[u] != Unmatched) continue;

            int start = graph.AdjOffset[u];
            int end = graph.AdjOffset[u + 1];
            for (int ai = start; ai < end; ai++)
            {
                int v = graph.AdjList[ai];
                if (match[v] == Unmatched)
                {
                    match[u] = v;
                    match[v] = u;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Heavy-Edge Matching: for each unmatched vertex, match with the
    /// heaviest-weight unmatched neighbor. See [1] Section 2.1.
    /// </summary>
    private static void HeavyEdgeMatching(GraphModel graph, int[] match, int[] order)
    {
        int n = graph.NodeCount;
        for (int oi = 0; oi < n; oi++)
        {
            int u = order[oi];
            if (match[u] != Unmatched) continue;

            int bestV = Unmatched;
            double bestW = double.NegativeInfinity;

            int start = graph.AdjOffset[u];
            int end = graph.AdjOffset[u + 1];
            for (int ai = start; ai < end; ai++)
            {
                int v = graph.AdjList[ai];
                if (match[v] != Unmatched) continue;
                double w = graph.AdjWeight[ai];
                if (w > bestW)
                {
                    bestW = w;
                    bestV = v;
                }
            }

            if (bestV != Unmatched)
            {
                match[u] = bestV;
                match[bestV] = u;
            }
        }
    }

    /// <summary>
    /// Stall-Reducing pass: reduces the number of unmatched vertices after
    /// the initial HEM pass. See [1] Section 2.2.
    ///
    /// For each unmatched vertex u:
    ///   1. Find u's heaviest MATCHED neighbor v.
    ///   2. Among v's UNMATCHED neighbors (excluding u), find the one w
    ///      with the heaviest edge to v.
    ///   3. Match (u, w) — they are "brothers" through v.
    ///
    /// When degreeGated = true, only consider matched neighbors v whose
    /// degree exceeds the threshold (HEMSRdeg variant).
    /// </summary>
    private static void StallReducing(
        GraphModel graph, int[] match, int[] order,
        bool degreeGated, double degreeThreshold)
    {
        int n = graph.NodeCount;
        for (int oi = 0; oi < n; oi++)
        {
            int u = order[oi];
            if (match[u] != Unmatched) continue;

            // Find heaviest matched neighbor v (the "parent" for brotherly matching)
            int bestV = Unmatched;
            double bestVWeight = double.NegativeInfinity;

            int uStart = graph.AdjOffset[u];
            int uEnd = graph.AdjOffset[u + 1];
            for (int ai = uStart; ai < uEnd; ai++)
            {
                int v = graph.AdjList[ai];
                if (match[v] == Unmatched) continue; // v must be matched
                if (degreeGated && graph.Degree[v] < degreeThreshold) continue;

                double w = graph.AdjWeight[ai];
                if (w > bestVWeight)
                {
                    bestVWeight = w;
                    bestV = v;
                }
            }

            if (bestV == Unmatched) continue;

            // Among bestV's unmatched neighbors (excluding u), find heaviest
            int bestW = Unmatched;
            double bestWWeight = double.NegativeInfinity;

            int vStart = graph.AdjOffset[bestV];
            int vEnd = graph.AdjOffset[bestV + 1];
            for (int ai = vStart; ai < vEnd; ai++)
            {
                int w = graph.AdjList[ai];
                if (w == u) continue;
                if (match[w] != Unmatched) continue;
                double wt = graph.AdjWeight[ai];
                if (wt > bestWWeight)
                {
                    bestWWeight = wt;
                    bestW = w;
                }
            }

            if (bestW != Unmatched)
            {
                match[u] = bestW;
                match[bestW] = u;
            }
        }
    }
}
