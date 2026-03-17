// ─────────────────────────────────────────────────────────────────────
// Clean-room implementation of QP-based partition refinement, derived
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
// ─────────────────────────────────────────────────────────────────────

namespace LumenGraph.Partition;

/// <summary>
/// QP-based partition refinement using continuous relaxation.
///
/// The key idea from [1],[3]: relax the discrete partition {0,1}^n to
/// a continuous problem over [0,1]^n. Each vertex i gets a continuous
/// variable x_i ∈ [0,1], where x_i = 0 means "fully in partition A"
/// and x_i = 1 means "fully in partition B".
///
/// The edge cut cost for edge (i,j) with weight w_ij is:
///   w_ij * (x_i - x_j)² = w_ij * (x_i² - 2·x_i·x_j + x_j²)
///
/// This is equivalent to minimizing:
///   f(x) = (1-x)ᵀ(D+A)x = xᵀDx - xᵀDx + xᵀDx - 2xᵀAx + xᵀA1 ...
///
/// More precisely, from [3] eq. (2.3):
///   minimize   (1-x)ᵀ(D+A)x
///   subject to lo ≤ aᵀx ≤ hi,  0 ≤ x ≤ 1
///
/// where:
///   D = diag(d_1,...,d_n), d_k = max edge weight incident to vertex k
///   A = adjacency matrix (a_ij = edge weight between i and j)
///   a = vertex weights (a_k = weight of vertex k)
///   lo, hi = bounds on total weight in partition B
///
/// The gradient of the objective at x is:
///   g_k = ∂f/∂x_k = d_k(2x_k - 1) + Σ_{j∈N(k)} w_kj(1 - 2x_j)
///       = (2x_k - 1)·d_k + Σ_{j∈N(k)} w_kj - 2·Σ_{j∈N(k)} w_kj·x_j
///
/// Simplified:   g_k = (x_k - 0.5)·D_k + Σ_{j∈N(k)} (0.5 - x_j)·w_kj
///               (using the fact that d_k ≥ Σw_kj guarantees convexity)
///
/// The algorithm alternates:
///   1. Gradient projection: steepest descent on the free set, then
///      project back onto the constraint set using the napsack algorithm.
///   2. Boundary refinement: push free variables to their bounds when
///      doing so reduces cost.
///   3. After convergence, round x to discrete: x_i > 0.5 → partition B.
/// </summary>
public static class QpRefine
{
    /// <summary>
    /// Refine a partition using QP continuous relaxation.
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

        // Ensure lo/hi are feasible
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, totalWeight);

        // Compute D[k] = max edge weight incident to vertex k.
        // This is the diagonal of the quadratic form. See [3] Section 2.
        var D = new double[n];
        for (int k = 0; k < n; k++)
        {
            double maxW = 0;
            int start = graph.AdjOffset[k];
            int end = graph.AdjOffset[k + 1];
            for (int ai = start; ai < end; ai++)
            {
                if (graph.AdjWeight[ai] > maxW)
                    maxW = graph.AdjWeight[ai];
            }
            D[k] = maxW;
        }

        // Initialize continuous relaxation from the discrete partition.
        // Boundary vertices (adjacent to the other side) get 0.25 or 0.75;
        // interior vertices get 0 or 1. See [1] Section 3.
        var x = new double[n];
        var isBoundary = new bool[n];
        DetectBoundary(graph, partition, isBoundary);

        for (int k = 0; k < n; k++)
        {
            if (partition.Side[k]) // partition B
                x[k] = isBoundary[k] ? 0.75 : 1.0;
            else // partition A
                x[k] = isBoundary[k] ? 0.25 : 0.0;
        }

        // Compute initial gradient
        var grad = new double[n];
        ComputeGradient(graph, x, D, grad);

        // FreeSet: vertices where 0 < x_i < 1
        // freeStatus[k]: 0 = free, +1 = bound at 1, -1 = bound at 0
        var freeStatus = new int[n];
        var freeList = new int[n];
        int freeCount = InitFreeSet(x, freeStatus, freeList, n);

        // Compute initial b = aᵀx (weighted sum of x)
        double b = 0;
        for (int k = 0; k < n; k++) b += vertexWeight[k] * x[k];

        // Adjust bounds to ensure initial feasibility
        if (b > hi) hi = b;
        if (b < lo) lo = b;

        int ib = ComputeIb(b, lo, hi);

        // Main iteration: alternate GradProj and Boundary phases
        // From [1] Section 3: "ImproveQP runs GradProj → Boundary →
        // GradProj → Boundary"
        for (int iter = 0; iter < options.QpIterations; iter++)
        {
            // Gradient projection step
            GradientProjection(
                graph, x, grad, D, vertexWeight,
                freeStatus, freeList, ref freeCount,
                ref b, ref ib, lo, hi, n);

            // Boundary step: push free variables to bounds
            BoundaryStep(
                graph, x, grad, D, vertexWeight,
                freeStatus, freeList, ref freeCount,
                ref b, ref ib, lo, hi, n);
        }

        // Round to discrete partition
        for (int k = 0; k < n; k++)
        {
            partition.Side[k] = x[k] > 0.5;
        }

        // Repair balance after rounding. The continuous relaxation
        // respects constraints, but rounding may violate them. Move
        // vertices with x closest to 0.5 (most ambiguous) from the
        // oversized side until the constraint is satisfied.
        RepairBalance(graph, partition, vertexWeight, x, lo, hi);

        partition.Recompute(graph);
    }

    /// <summary>
    /// After rounding x to discrete, check if the balance constraint
    /// is violated. If so, move the borderline vertices (x closest to 0.5)
    /// from the oversized side until weight in B is within [lo, hi].
    /// </summary>
    private static void RepairBalance(
        GraphModel graph, PartitionResult partition,
        double[] vertexWeight, double[] x, double lo, double hi)
    {
        int n = graph.NodeCount;
        double weightB = 0;
        for (int i = 0; i < n; i++)
            if (partition.Side[i]) weightB += vertexWeight[i];

        if (weightB >= lo && weightB <= hi) return; // already balanced

        if (weightB > hi)
        {
            // B is too large — move B vertices closest to 0.5 to A.
            // Sort B vertices by x value ascending (closest to 0.5 first).
            var candidates = new List<(int idx, double xVal)>();
            for (int i = 0; i < n; i++)
                if (partition.Side[i])
                    candidates.Add((i, x[i]));
            candidates.Sort((a, b) => a.xVal.CompareTo(b.xVal));

            foreach (var (idx, _) in candidates)
            {
                if (weightB <= hi) break;
                partition.Side[idx] = false;
                weightB -= vertexWeight[idx];
            }
        }
        else // weightB < lo
        {
            // B is too small — move A vertices closest to 0.5 to B.
            var candidates = new List<(int idx, double xVal)>();
            for (int i = 0; i < n; i++)
                if (!partition.Side[i])
                    candidates.Add((i, x[i]));
            candidates.Sort((a, b) => b.xVal.CompareTo(a.xVal)); // descending

            foreach (var (idx, _) in candidates)
            {
                if (weightB >= lo) break;
                partition.Side[idx] = true;
                weightB += vertexWeight[idx];
            }
        }
    }

    /// <summary>
    /// Compute the gradient of the QP objective at the current x.
    /// g_k = (x_k - 0.5) * D_k + Σ_{j∈N(k)} (0.5 - x_j) * w_kj
    /// </summary>
    private static void ComputeGradient(
        GraphModel graph, double[] x, double[] D, double[] grad)
    {
        int n = graph.NodeCount;
        for (int k = 0; k < n; k++)
        {
            grad[k] = (x[k] - 0.5) * D[k];
        }

        for (int k = 0; k < n; k++)
        {
            double xk = x[k];
            double r = 0.5 - xk;
            int start = graph.AdjOffset[k];
            int end = graph.AdjOffset[k + 1];
            for (int ai = start; ai < end; ai++)
            {
                grad[graph.AdjList[ai]] += r * graph.AdjWeight[ai];
            }
        }
    }

    /// <summary>
    /// Initialize the free set from the current x values.
    /// </summary>
    private static int InitFreeSet(
        double[] x, int[] freeStatus, int[] freeList, int n)
    {
        int count = 0;
        for (int k = 0; k < n; k++)
        {
            if (x[k] >= 1.0)
                freeStatus[k] = 1;
            else if (x[k] <= 0.0)
                freeStatus[k] = -1;
            else
            {
                freeStatus[k] = 0;
                freeList[count++] = k;
            }
        }
        return count;
    }

    /// <summary>
    /// Gradient projection: take a steepest-descent step on the free set,
    /// then project back onto the constraint set.
    ///
    /// From [3] Section 3: the step size is computed via the Rayleigh
    /// quotient on the free set:
    ///   α = gᵀg / gᵀ(D+A)g
    /// where g is the gradient restricted to the free set.
    /// </summary>
    private static void GradientProjection(
        GraphModel graph, double[] x, double[] grad, double[] D,
        double[] vertexWeight,
        int[] freeStatus, int[] freeList, ref int freeCount,
        ref double b, ref int ib, double lo, double hi, int n)
    {
        if (freeCount == 0) return;

        // Compute gradient norm on the free set: gᵀg
        double gg = 0;
        for (int fi = 0; fi < freeCount; fi++)
        {
            int k = freeList[fi];
            gg += grad[k] * grad[k];
        }

        if (gg < 1e-30) return; // converged

        // Compute gᵀ(D+A)g — the curvature along the gradient direction.
        // (D+A)g_k = D_k·g_k + Σ_{j∈N(k)} w_kj·g_j
        // We only need the dot product with g restricted to the free set.
        double gDg = 0;
        for (int fi = 0; fi < freeCount; fi++)
        {
            int k = freeList[fi];
            double dk_gk = D[k] * grad[k];

            double Ag_k = 0;
            int start = graph.AdjOffset[k];
            int end = graph.AdjOffset[k + 1];
            for (int ai = start; ai < end; ai++)
            {
                int j = graph.AdjList[ai];
                if (freeStatus[j] == 0)
                    Ag_k += graph.AdjWeight[ai] * grad[j];
            }

            gDg += grad[k] * (dk_gk + Ag_k);
        }

        if (gDg < 1e-30) return; // flat or negative curvature

        // Step size from Rayleigh quotient
        double alpha = gg / gDg;

        // Take the step: x_k -= alpha * g_k, clamp to [0,1]
        for (int fi = 0; fi < freeCount; fi++)
        {
            int k = freeList[fi];
            double newX = x[k] - alpha * grad[k];
            newX = Math.Clamp(newX, 0.0, 1.0);

            double dx = newX - x[k];
            if (dx == 0) continue;

            // Update gradient for neighbors
            int start = graph.AdjOffset[k];
            int end = graph.AdjOffset[k + 1];
            for (int ai = start; ai < end; ai++)
            {
                grad[graph.AdjList[ai]] += dx * graph.AdjWeight[ai];
            }
            grad[k] += dx * D[k];

            b += dx * vertexWeight[k];
            x[k] = newX;
        }

        // Project onto the constraint set: lo ≤ aᵀx ≤ hi
        // This is the napsack projection from [2].
        NapsackProject(x, grad, D, vertexWeight, freeStatus, freeList,
                       ref freeCount, ref b, ref ib, lo, hi, n, graph);
    }

    /// <summary>
    /// Napsack projection: project x onto the constraint set
    ///   {x : 0 ≤ x ≤ 1, lo ≤ aᵀx ≤ hi}
    ///
    /// From [2]: solve the dual problem
    ///   min ||x - y||² subject to 0 ≤ x ≤ 1, lo ≤ aᵀx ≤ hi
    /// using a Lagrange multiplier λ for the linear constraint.
    /// The optimal x_k = clamp(y_k - λ·a_k/D_k, 0, 1).
    ///
    /// The dual is solved by finding the right λ using a breakpoint
    /// algorithm with O(n + h·log n) complexity where h is the number
    /// of breakpoints (transitions between bound and free).
    ///
    /// Simplified version: if b is within [lo,hi], no projection needed.
    /// Otherwise, uniformly scale free variables to bring b back in range.
    /// </summary>
    private static void NapsackProject(
        double[] x, double[] grad, double[] D, double[] vertexWeight,
        int[] freeStatus, int[] freeList, ref int freeCount,
        ref double b, ref int ib, double lo, double hi, int n,
        GraphModel graph)
    {
        // If already feasible, just update ib
        if (b >= lo && b <= hi)
        {
            ib = ComputeIb(b, lo, hi);
            return;
        }

        // Need to adjust: compute how much weight is in the free set and
        // which direction to move
        double freeWeightedSum = 0;
        double freeMaxShift = 0;

        if (b > hi)
        {
            // Need to decrease b. Reduce x values of free vertices.
            double excess = b - hi;

            for (int fi = 0; fi < freeCount; fi++)
            {
                int k = freeList[fi];
                freeWeightedSum += vertexWeight[k] * x[k];
            }

            if (freeWeightedSum < 1e-15) return; // can't adjust

            // Scale free variables down proportionally
            double scale = Math.Max(0, 1.0 - excess / freeWeightedSum);
            for (int fi = 0; fi < freeCount; fi++)
            {
                int k = freeList[fi];
                double oldX = x[k];
                double newX = oldX * scale;
                newX = Math.Clamp(newX, 0.0, 1.0);
                double dx = newX - oldX;
                if (dx == 0) continue;

                b += dx * vertexWeight[k];

                // Update gradient
                int start = graph.AdjOffset[k];
                int end = graph.AdjOffset[k + 1];
                for (int ai = start; ai < end; ai++)
                    grad[graph.AdjList[ai]] += dx * graph.AdjWeight[ai];
                grad[k] += dx * D[k];

                x[k] = newX;
            }
        }
        else // b < lo
        {
            // Need to increase b. Increase x values of free vertices.
            double deficit = lo - b;

            for (int fi = 0; fi < freeCount; fi++)
            {
                int k = freeList[fi];
                freeMaxShift += vertexWeight[k] * (1.0 - x[k]);
            }

            if (freeMaxShift < 1e-15) return; // can't adjust

            double scale = Math.Min(1.0, deficit / freeMaxShift);
            for (int fi = 0; fi < freeCount; fi++)
            {
                int k = freeList[fi];
                double oldX = x[k];
                double newX = oldX + (1.0 - oldX) * scale;
                newX = Math.Clamp(newX, 0.0, 1.0);
                double dx = newX - oldX;
                if (dx == 0) continue;

                b += dx * vertexWeight[k];

                int start = graph.AdjOffset[k];
                int end = graph.AdjOffset[k + 1];
                for (int ai = start; ai < end; ai++)
                    grad[graph.AdjList[ai]] += dx * graph.AdjWeight[ai];
                grad[k] += dx * D[k];

                x[k] = newX;
            }
        }

        // Update free set: remove newly-bound variables
        int newFreeCount = 0;
        for (int fi = 0; fi < freeCount; fi++)
        {
            int k = freeList[fi];
            if (x[k] <= 0.0)
            {
                x[k] = 0.0;
                freeStatus[k] = -1;
            }
            else if (x[k] >= 1.0)
            {
                x[k] = 1.0;
                freeStatus[k] = 1;
            }
            else
            {
                freeList[newFreeCount++] = k;
            }
        }
        freeCount = newFreeCount;
        ib = ComputeIb(b, lo, hi);
    }

    /// <summary>
    /// Boundary step: for each free variable, check if pushing it to 0 or 1
    /// would reduce the cost while maintaining feasibility.
    /// Also check if bound variables can be flipped (0→1 or 1→0).
    ///
    /// From [1] Section 3: "QPBoundary moves components of x to the feasible
    /// region boundary while decreasing the cost function."
    /// </summary>
    private static void BoundaryStep(
        GraphModel graph, double[] x, double[] grad, double[] D,
        double[] vertexWeight,
        int[] freeStatus, int[] freeList, ref int freeCount,
        ref double b, ref int ib, double lo, double hi, int n)
    {
        // Step 1: For each free variable, try pushing to 0 or 1
        int newFreeCount = 0;
        for (int fi = 0; fi < freeCount; fi++)
        {
            int k = freeList[fi];
            if (ib != 0 || freeStatus[k] != 0)
            {
                // b is at a bound, or k is no longer free; keep it
                if (freeStatus[k] == 0)
                    freeList[newFreeCount++] = k;
                continue;
            }

            double ak = vertexWeight[k];

            if (grad[k] > 0) // cost decreases if x_k decreases
            {
                double maxDecrease = (b - lo) / Math.Max(ak, 1e-15);
                if (maxDecrease >= x[k])
                {
                    // Push x_k to 0
                    double dx = -x[k];
                    ApplyDelta(graph, grad, D, k, dx);
                    b += dx * ak;
                    x[k] = 0;
                    freeStatus[k] = -1;
                    // removed from free set
                }
                else if (maxDecrease > 0)
                {
                    // Partial decrease; b hits lo
                    double dx = -maxDecrease;
                    ApplyDelta(graph, grad, D, k, dx);
                    x[k] += dx;
                    b = lo;
                    ib = -1;
                    freeList[newFreeCount++] = k;
                }
                else
                {
                    freeList[newFreeCount++] = k;
                }
            }
            else // grad[k] <= 0, cost decreases if x_k increases
            {
                double maxIncrease = (hi - b) / Math.Max(ak, 1e-15);
                if (maxIncrease >= 1.0 - x[k])
                {
                    // Push x_k to 1
                    double dx = 1.0 - x[k];
                    ApplyDelta(graph, grad, D, k, dx);
                    b += dx * ak;
                    x[k] = 1.0;
                    freeStatus[k] = 1;
                    // removed from free set
                }
                else if (maxIncrease > 0)
                {
                    double dx = maxIncrease;
                    ApplyDelta(graph, grad, D, k, dx);
                    x[k] += dx;
                    b = hi;
                    ib = 1;
                    freeList[newFreeCount++] = k;
                }
                else
                {
                    freeList[newFreeCount++] = k;
                }
            }
        }
        freeCount = newFreeCount;

        // Step 2: For each bound variable, try flipping (0→1 or 1→0)
        for (int k = 0; k < n; k++)
        {
            if (freeStatus[k] == 0) continue; // free, handled above

            double ak = vertexWeight[k];
            if (freeStatus[k] > 0) // x_k = 1, try flipping to 0
            {
                if (b - ak >= lo)
                {
                    // Cost change for x_k: 1→0 is ΔC = 0.5·D_k + g_k
                    if (0.5 * D[k] + grad[k] >= 0) // flip reduces cost
                    {
                        ApplyDelta(graph, grad, D, k, -1.0);
                        b -= ak;
                        x[k] = 0.0;
                        freeStatus[k] = -1;
                        ib = ComputeIb(b, lo, hi);
                    }
                }
            }
            else // x_k = 0, try flipping to 1
            {
                if (b + ak <= hi)
                {
                    // Cost change for x_k: 0→1 is ΔC = g_k - 0.5·D_k
                    if (grad[k] - 0.5 * D[k] <= 0) // flip reduces cost
                    {
                        ApplyDelta(graph, grad, D, k, 1.0);
                        b += ak;
                        x[k] = 1.0;
                        freeStatus[k] = 1;
                        ib = ComputeIb(b, lo, hi);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Apply a change dx to x[k] and update the gradient of all neighbors.
    /// </summary>
    private static void ApplyDelta(
        GraphModel graph, double[] grad, double[] D, int k, double dx)
    {
        int start = graph.AdjOffset[k];
        int end = graph.AdjOffset[k + 1];
        for (int ai = start; ai < end; ai++)
        {
            grad[graph.AdjList[ai]] += dx * graph.AdjWeight[ai];
        }
        grad[k] += dx * D[k];
    }

    /// <summary>
    /// Detect boundary vertices: vertices that have at least one neighbor
    /// in the opposite partition.
    /// </summary>
    private static void DetectBoundary(
        GraphModel graph, PartitionResult partition, bool[] isBoundary)
    {
        int n = graph.NodeCount;
        for (int k = 0; k < n; k++)
        {
            isBoundary[k] = false;
            bool side = partition.Side[k];
            int start = graph.AdjOffset[k];
            int end = graph.AdjOffset[k + 1];
            for (int ai = start; ai < end; ai++)
            {
                if (partition.Side[graph.AdjList[ai]] != side)
                {
                    isBoundary[k] = true;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Compute the constraint status indicator:
    ///   -1 if b ≤ lo, +1 if b ≥ hi, 0 if lo &lt; b &lt; hi.
    /// </summary>
    private static int ComputeIb(double b, double lo, double hi)
    {
        if (b <= lo) return -1;
        if (b >= hi) return 1;
        return 0;
    }
}
