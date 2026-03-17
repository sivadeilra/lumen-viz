// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation derived from the published algorithms in:
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
// papers and does NOT derive from the Mongoose source code (which is
// licensed under GPL-3.0). This is an independent implementation.
// ─────────────────────────────────────────────────────────────────────

namespace LumenGraph.Partition;

/// <summary>
/// Result of a graph bisection. Stores the partition assignment for each
/// vertex (false = partition A, true = partition B) and the edge cut cost.
/// </summary>
public sealed class PartitionResult
{
    /// <summary>Partition assignment per vertex. false = A, true = B.</summary>
    public bool[] Side { get; }

    /// <summary>Number of vertices in the graph.</summary>
    public int N => Side.Length;

    /// <summary>Edge cut: sum of weights of edges crossing the partition.</summary>
    public double EdgeCut { get; set; }

    /// <summary>Number of vertices in partition A.</summary>
    public int SizeA { get; set; }

    /// <summary>Number of vertices in partition B.</summary>
    public int SizeB { get; set; }

    /// <summary>Imbalance ratio: max(|A|,|B|) / (n/2). 1.0 = perfectly balanced.</summary>
    public double Imbalance => N > 0 ? 2.0 * Math.Max(SizeA, SizeB) / N : 1.0;

    public PartitionResult(int n)
    {
        Side = new bool[n];
    }

    /// <summary>
    /// Recompute SizeA, SizeB, and EdgeCut from the current Side[] assignment.
    /// </summary>
    public void Recompute(GraphModel graph)
    {
        SizeA = 0;
        SizeB = 0;
        for (int i = 0; i < Side.Length; i++)
        {
            if (Side[i]) SizeB++;
            else SizeA++;
        }

        double cut = 0;
        for (int e = 0; e < graph.EdgeCount; e++)
        {
            if (Side[graph.EdgeSource[e]] != Side[graph.EdgeTarget[e]])
                cut += graph.EdgeWeight[e];
        }
        EdgeCut = cut;
    }
}

/// <summary>
/// Options controlling the multilevel edge separator algorithm.
/// Default values follow the recommendations in [1].
/// </summary>
public sealed class PartitionOptions
{
    /// <summary>
    /// Target split ratio for partition sizes. 0.5 = balanced bisection.
    /// The algorithm targets: |A| ≈ target * n, |B| ≈ (1 - target) * n.
    /// </summary>
    public double TargetSplit { get; set; } = 0.5;

    /// <summary>
    /// Maximum allowed imbalance tolerance. The partition will satisfy:
    ///   (1 - tolerance) * target * n ≤ |A| ≤ (1 + tolerance) * target * n
    /// </summary>
    public double BalanceTolerance { get; set; } = 0.25;

    /// <summary>Matching strategy to use during coarsening.</summary>
    public MatchingStrategy Matching { get; set; } = MatchingStrategy.HEMSRdeg;

    /// <summary>Stop coarsening when graph has fewer than this many nodes.</summary>
    public int CoarsenLimit { get; set; } = 64;

    /// <summary>
    /// Maximum coarsening levels. Prevents runaway coarsening on graphs
    /// that don't reduce well.
    /// </summary>
    public int MaxLevels { get; set; } = 64;

    /// <summary>Number of QP gradient projection iterations per refinement.</summary>
    public int QpIterations { get; set; } = 10;

    /// <summary>Number of FM passes per refinement.</summary>
    public int FmPasses { get; set; } = 4;

    /// <summary>Random seed for reproducibility.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>
    /// If true, use random matching. If false, use the strategy in
    /// <see cref="Matching"/>.
    /// </summary>
    public bool RandomMatch { get; set; } = false;

    /// <summary>
    /// Degree threshold multiplier for HEMSRdeg. Vertices with degree
    /// greater than this multiple of the average degree are eligible
    /// for stall-reducing brotherly matching. See [1] Section 2.2.
    /// </summary>
    public double DegreeThreshold { get; set; } = 2.0;
}

/// <summary>
/// Matching strategy for the coarsening phase. See [1] Section 2.
/// </summary>
public enum MatchingStrategy
{
    /// <summary>Random matching — fast but lower quality.</summary>
    Random,

    /// <summary>Heavy-Edge Matching — greedy match with heaviest neighbor.</summary>
    HEM,

    /// <summary>HEM with Stall-Reducing passes — reduces unmatched vertices.</summary>
    HEMSR,

    /// <summary>
    /// HEM with degree-gated Stall-Reducing — only applies SR to high-degree
    /// vertices. Best quality/cost tradeoff per [1] Section 4.
    /// </summary>
    HEMSRdeg,
}
