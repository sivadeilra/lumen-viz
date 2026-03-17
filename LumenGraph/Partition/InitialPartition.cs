// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of initial partitioning for multilevel
// graph bisection, derived from the published algorithms in:
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
/// Initial partitioning of the coarsest graph in the multilevel hierarchy.
///
/// Since the coarsest graph is small (typically &lt; 64 vertices), the
/// initial partition does not need to be highly optimized — it will be
/// refined during uncoarsening. Multiple strategies are tried and the
/// best (lowest edge cut) is kept.
///
/// From [1] Section 3: The initial partition is computed on the coarsest
/// graph using a combination of random bisection and BFS-based bisection,
/// taking the best result.
/// </summary>
public static class InitialPartition
{
    /// <summary>
    /// Compute an initial bisection of the graph. Tries multiple strategies
    /// and returns the one with the lowest edge cut.
    /// </summary>
    public static PartitionResult Compute(
        GraphModel graph, double[] vertexWeight, PartitionOptions options)
    {
        int n = graph.NodeCount;
        if (n == 0) return new PartitionResult(0);

        double totalWeight = 0;
        for (int i = 0; i < n; i++) totalWeight += vertexWeight[i];

        double targetA = options.TargetSplit * totalWeight;
        double lo = targetA * (1 - options.BalanceTolerance);
        double hi = targetA * (1 + options.BalanceTolerance);

        var rng = new Random(options.Seed);

        // Try multiple random bisections and keep the best
        PartitionResult? best = null;

        // Strategy 1: BFS-based bisection from multiple seeds
        for (int trial = 0; trial < 4; trial++)
        {
            int seed = rng.Next(n);
            var result = BfsBisection(graph, vertexWeight, seed, lo, hi, targetA);
            if (best == null || result.EdgeCut < best.EdgeCut)
                best = result;
        }

        // Strategy 2: Random bisections
        for (int trial = 0; trial < 4; trial++)
        {
            var result = RandomBisection(graph, vertexWeight, rng, lo, hi, targetA);
            if (result.EdgeCut < best!.EdgeCut)
                best = result;
        }

        // Strategy 3: Greedy growing from highest-degree vertex
        {
            int maxDegNode = 0;
            for (int i = 1; i < n; i++)
                if (graph.Degree[i] > graph.Degree[maxDegNode])
                    maxDegNode = i;
            var result = BfsBisection(graph, vertexWeight, maxDegNode, lo, hi, targetA);
            if (result.EdgeCut < best!.EdgeCut)
                best = result;
        }

        return best!;
    }

    /// <summary>
    /// BFS-based bisection: grow partition A from a seed vertex using BFS,
    /// stopping when the weight target is reached.
    /// </summary>
    private static PartitionResult BfsBisection(
        GraphModel graph, double[] vertexWeight,
        int seed, double lo, double hi, double targetA)
    {
        int n = graph.NodeCount;
        var result = new PartitionResult(n);

        // Start with all vertices in B (Side = true)
        Array.Fill(result.Side, true);

        var visited = new bool[n];
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        visited[seed] = true;

        double weightA = 0;

        while (queue.Count > 0 && weightA < targetA)
        {
            int u = queue.Dequeue();
            result.Side[u] = false; // move to A
            weightA += vertexWeight[u];

            int start = graph.AdjOffset[u];
            int end = graph.AdjOffset[u + 1];
            for (int ai = start; ai < end; ai++)
            {
                int v = graph.AdjList[ai];
                if (!visited[v])
                {
                    visited[v] = true;
                    queue.Enqueue(v);
                }
            }
        }

        result.Recompute(graph);
        return result;
    }

    /// <summary>
    /// Random bisection: randomly assign vertices to partition A until the
    /// weight target is approximately reached.
    /// </summary>
    private static PartitionResult RandomBisection(
        GraphModel graph, double[] vertexWeight,
        Random rng, double lo, double hi, double targetA)
    {
        int n = graph.NodeCount;
        var result = new PartitionResult(n);

        // Create random permutation
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        rng.Shuffle(perm);

        double weightA = 0;
        for (int i = 0; i < n; i++)
        {
            int u = perm[i];
            if (weightA + vertexWeight[u] <= targetA * 1.1)
            {
                result.Side[u] = false; // A
                weightA += vertexWeight[u];
            }
            else
            {
                result.Side[u] = true; // B
            }
        }

        result.Recompute(graph);
        return result;
    }
}
