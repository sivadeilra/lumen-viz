// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of Fiduccia-Mattheyses partition refinement,
// derived from the published algorithms in:
//
//   [1] T. A. Davis, W. W. Hager, S. P. Kolodziej, S. N. Yeralan,
//       "Algorithm 1003: Mongoose, A Graph Coarsening and Partitioning
//       Library," ACM Trans. Math. Softw. 46(1), Article 7, March 2020.
//       https://doi.org/10.1145/3337792
//
//   [4] C. M. Fiduccia, R. M. Mattheyses,
//       "A Linear-Time Heuristic for Improving Network Partitions,"
//       19th Design Automation Conference, pp. 175–181, 1982.
//
// The FM algorithm as used in Mongoose is described in [1] Section 3:
// after the QP refinement rounds x to a discrete partition, FM is used
// to further improve the cut by performing local vertex swaps.
//
// This code was written from the mathematical descriptions in the above
// papers and does NOT derive from the Mongoose source code (GPL-3.0).
// This is an independent implementation.
// ─────────────────────────────────────────────────────────────────────

namespace LumenGraph.Partition;

/// <summary>
/// Fiduccia-Mattheyses (FM) partition refinement.
///
/// FM is a local search heuristic that improves graph bisections by
/// moving vertices across the partition boundary. The key ideas:
///
/// 1. For each boundary vertex, compute its "gain" = reduction in edge
///    cut if moved to the other side.
/// 2. Move the highest-gain vertex that maintains balance constraints.
/// 3. Lock it (don't move again this pass).
/// 4. Repeat until no unlocked boundary vertices remain.
/// 5. Among all partial move sequences, keep the prefix that gave the
///    minimum total cut. This allows the algorithm to escape local minima
///    by making some bad moves followed by good ones.
///
/// Time: O(|E|) per pass. Typically converges in a few passes.
/// </summary>
public static class FmRefine
{
    /// <summary>
    /// Refine a partition using FM local search.
    /// Modifies the partition result in place.
    /// </summary>
    public static void Refine(
        GraphModel graph, PartitionResult partition,
        double[] vertexWeight, PartitionOptions options)
    {
        int n = graph.NodeCount;
        if (n <= 1) return;

        double totalWeight = 0;
        for (int i = 0; i < n; i++) totalWeight += vertexWeight[i];

        double targetB = (1 - options.TargetSplit) * totalWeight;
        double lo = targetB * (1 - options.BalanceTolerance);
        double hi = targetB * (1 + options.BalanceTolerance);
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, totalWeight);

        // Compute initial weight in B
        double weightB = 0;
        for (int i = 0; i < n; i++)
            if (partition.Side[i]) weightB += vertexWeight[i];

        // Compute gain for each vertex: how much the edge cut decreases
        // if we move vertex i to the other side.
        // gain[i] = Σ_{j∈N(i)} w_ij * (side[j] == side[i] ? 1 : -1)
        // (positive gain = moving i reduces cut)
        // Wait — that's: external_weight - internal_weight
        // external = edges to vertices on the OTHER side
        // internal = edges to vertices on the SAME side
        // gain = external - internal  (moving saves external, costs internal)
        var gain = new double[n];
        ComputeGains(graph, partition, gain);

        var locked = new bool[n];
        var moveLog = new (int vertex, bool newSide, double cutDelta)[n];

        for (int pass = 0; pass < options.FmPasses; pass++)
        {
            Array.Clear(locked);
            int moveCount = 0;
            double runningCutDelta = 0;
            double bestCutDelta = 0;
            int bestMoveCount = 0;

            // Recompute gains at start of each pass
            ComputeGains(graph, partition, gain);

            for (int step = 0; step < n; step++)
            {
                // Find highest-gain unlocked vertex that maintains balance
                int bestV = -1;
                double bestGain = double.NegativeInfinity;

                for (int i = 0; i < n; i++)
                {
                    if (locked[i]) continue;
                    if (gain[i] <= bestGain) continue;

                    // Check balance constraint if we move i
                    double newWeightB = partition.Side[i]
                        ? weightB - vertexWeight[i]  // moving from B to A
                        : weightB + vertexWeight[i];  // moving from A to B

                    if (newWeightB >= lo && newWeightB <= hi)
                    {
                        bestGain = gain[i];
                        bestV = i;
                    }
                }

                if (bestV < 0) break; // no feasible move

                // Execute the move
                bool newSide = !partition.Side[bestV];
                partition.Side[bestV] = newSide;
                locked[bestV] = true;

                // Update weightB
                if (newSide) // moved to B
                    weightB += vertexWeight[bestV];
                else // moved to A
                    weightB -= vertexWeight[bestV];

                // Update gains of neighbors
                UpdateGains(graph, partition, gain, bestV);

                double cutDelta = -bestGain; // negative gain = cut increases
                runningCutDelta += cutDelta;

                moveLog[moveCount] = (bestV, newSide, cutDelta);
                moveCount++;

                if (runningCutDelta < bestCutDelta)
                {
                    bestCutDelta = runningCutDelta;
                    bestMoveCount = moveCount;
                }
            }

            // Roll back to the best prefix of moves
            if (bestMoveCount < moveCount)
            {
                for (int i = moveCount - 1; i >= bestMoveCount; i--)
                {
                    var (v, side, _) = moveLog[i];
                    partition.Side[v] = !side; // undo

                    if (!side) // was moved to A, undo → back to B
                        weightB += vertexWeight[v];
                    else
                        weightB -= vertexWeight[v];
                }
            }

            // If no improvement was made this pass, we're done
            if (bestCutDelta >= -1e-12)
                break;
        }

        partition.Recompute(graph);
    }

    /// <summary>
    /// Compute gain for every vertex.
    /// gain[i] = external_cost(i) - internal_cost(i)
    /// where external = sum of edge weights to vertices on the other side,
    ///       internal = sum of edge weights to vertices on the same side.
    /// Positive gain means moving i reduces the cut.
    /// </summary>
    private static void ComputeGains(
        GraphModel graph, PartitionResult partition, double[] gain)
    {
        int n = graph.NodeCount;
        for (int i = 0; i < n; i++)
        {
            bool sideI = partition.Side[i];
            double ext = 0, intl = 0;

            int start = graph.AdjOffset[i];
            int end = graph.AdjOffset[i + 1];
            for (int ai = start; ai < end; ai++)
            {
                double w = graph.AdjWeight[ai];
                if (partition.Side[graph.AdjList[ai]] != sideI)
                    ext += w;
                else
                    intl += w;
            }
            gain[i] = ext - intl;
        }
    }

    /// <summary>
    /// After moving vertex v to its new side, update gains of its neighbors.
    /// When v moves from side S to side !S:
    /// - For neighbor j on the same original side S: j lost an internal edge
    ///   and gained an external edge → gain[j] += 2·w(v,j)
    /// - For neighbor j on the other side !S: j gained an internal edge
    ///   and lost an external edge → gain[j] -= 2·w(v,j)
    /// </summary>
    private static void UpdateGains(
        GraphModel graph, PartitionResult partition, double[] gain, int v)
    {
        bool newSideV = partition.Side[v];

        int start = graph.AdjOffset[v];
        int end = graph.AdjOffset[v + 1];
        for (int ai = start; ai < end; ai++)
        {
            int j = graph.AdjList[ai];
            double w = graph.AdjWeight[ai];

            if (partition.Side[j] == newSideV)
            {
                // j is now on the same side as v → v's move made (v,j) internal
                gain[j] -= 2 * w;
            }
            else
            {
                // j is on the opposite side → v's move made (v,j) external
                gain[j] += 2 * w;
            }
        }
    }
}
