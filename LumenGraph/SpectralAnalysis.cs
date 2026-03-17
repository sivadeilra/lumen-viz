using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace LumenGraph;

/// <summary>
/// Spectral analysis of graphs via the graph Laplacian.
///
/// The graph Laplacian L = D - A, where D is the degree diagonal and A the
/// adjacency matrix. Its eigenvalues reveal fundamental graph structure:
///   - λ₁ = 0 always (constant eigenvector)
///   - λ₂ (algebraic connectivity / Fiedler value): how well-connected the graph is
///   - The Fiedler vector (eigenvector of λ₂): optimal 1D embedding for bandwidth
///     minimization, graph bisection, and node ordering
///
/// For graphs up to ~8000 nodes, full eigendecomposition is used (MathNet dense EVD).
/// For larger graphs, inverse iteration targets just the Fiedler vector.
/// </summary>
public static class SpectralAnalysis
{
    /// <summary>
    /// Maximum node count for full dense eigendecomposition.
    /// Above this threshold, inverse iteration is used for the Fiedler vector only.
    /// </summary>
    private const int DenseThreshold = 8000;

    /// <summary>
    /// Compute the Fiedler vector (eigenvector of the second-smallest eigenvalue
    /// of the graph Laplacian). Returns double[N] where values encode optimal 1D
    /// node positions for bandwidth minimization / spectral ordering.
    ///
    /// Returns null if the graph has fewer than 2 nodes.
    /// </summary>
    public static double[]? FiedlerVector(GraphModel graph)
    {
        int n = graph.NodeCount;
        if (n < 2) return null;

        if (n <= DenseThreshold)
            return FiedlerVectorDense(graph);
        else
            return FiedlerVectorIterative(graph);
    }

    /// <summary>
    /// Compute the algebraic connectivity (second-smallest Laplacian eigenvalue).
    /// A measure of how well-connected the graph is: 0 means disconnected,
    /// higher values mean more robust connectivity.
    /// </summary>
    public static double AlgebraicConnectivity(GraphModel graph)
    {
        int n = graph.NodeCount;
        if (n < 2) return 0;

        if (n <= DenseThreshold)
        {
            var eigenvalues = LaplacianEigenvalues(graph);
            return eigenvalues[1]; // second-smallest
        }
        else
        {
            // For large graphs, use Rayleigh quotient with the Fiedler vector
            var fiedler = FiedlerVectorIterative(graph);
            if (fiedler == null) return 0;
            return RayleighQuotient(graph, fiedler);
        }
    }

    /// <summary>
    /// Compute all eigenvalues of the graph Laplacian (sorted ascending).
    /// Only practical for N ≤ DenseThreshold.
    /// </summary>
    public static double[] LaplacianEigenvalues(GraphModel graph)
    {
        int n = graph.NodeCount;
        if (n < 1) return [];
        if (n > DenseThreshold)
            throw new InvalidOperationException(
                $"Dense eigendecomposition not supported for N={n} > {DenseThreshold}. " +
                "Use FiedlerVector() or AlgebraicConnectivity() which auto-select methods.");

        var L = BuildLaplacian(graph);
        var evd = L.Evd();
        var vals = evd.EigenValues.Select(c => c.Real).OrderBy(v => v).ToArray();
        return vals;
    }

    /// <summary>
    /// Compute the spectral ordering: nodes sorted by their Fiedler vector values.
    /// This minimizes the matrix bandwidth and reveals the graph's natural linear
    /// structure. Returns int[N] where ordering[0] is the node at one "end" and
    /// ordering[N-1] is at the other.
    /// </summary>
    public static int[]? SpectralOrdering(GraphModel graph)
    {
        var fiedler = FiedlerVector(graph);
        if (fiedler == null) return null;

        return Enumerable.Range(0, graph.NodeCount)
            .OrderBy(i => fiedler[i])
            .ToArray();
    }

    // ════════════════════════════════════════════════════════════════════
    // Dense path (N ≤ threshold)
    // ════════════════════════════════════════════════════════════════════

    private static Matrix<double> BuildLaplacian(GraphModel graph)
    {
        int n = graph.NodeCount;
        var L = DenseMatrix.Create(n, n, 0.0);

        var src = graph.EdgeSource;
        var tgt = graph.EdgeTarget;
        int edgeCount = graph.EdgeCount;

        for (int e = 0; e < edgeCount; e++)
        {
            int u = src[e], v = tgt[e];
            L[u, v] -= 1.0;
            L[v, u] -= 1.0;
            L[u, u] += 1.0;
            L[v, v] += 1.0;
        }

        return L;
    }

    private static double[]? FiedlerVectorDense(GraphModel graph)
    {
        int n = graph.NodeCount;
        var L = BuildLaplacian(graph);
        var evd = L.Evd();

        // Eigenvalues sorted ascending; pick the second-smallest
        var eigenPairs = evd.EigenValues
            .Select((val, idx) => (val: val.Real, idx))
            .OrderBy(p => p.val)
            .ToArray();

        if (eigenPairs.Length < 2) return null;

        int fiedlerIdx = eigenPairs[1].idx;
        var vec = evd.EigenVectors.Column(fiedlerIdx);
        return vec.ToArray();
    }

    // ════════════════════════════════════════════════════════════════════
    // Iterative path (large graphs)
    // Uses inverse iteration with shift to target the smallest non-zero
    // eigenvalue of the Laplacian.
    // ════════════════════════════════════════════════════════════════════

    private const int MaxIterations = 300;
    private const double Tolerance = 1e-8;

    private static double[]? FiedlerVectorIterative(GraphModel graph)
    {
        int n = graph.NodeCount;
        if (n < 2) return null;

        // Laplacian-vector multiply using CSR adjacency
        // L*x = D*x - A*x
        void LaplacianMultiply(double[] x, double[] result)
        {
            var degree = graph.Degree;
            var adjOff = graph.AdjOffset;
            var adjList = graph.AdjList;

            for (int i = 0; i < n; i++)
            {
                double sum = degree[i] * x[i];
                int start = adjOff[i], end = adjOff[i + 1];
                for (int j = start; j < end; j++)
                    sum -= x[adjList[j]];
                result[i] = sum;
            }
        }

        // Power iteration on (L + shift*I)^{-1} with shift near 0.
        // Instead of matrix inversion, solve (L + shift*I) * y = x
        // using conjugate gradient (L is symmetric positive semi-definite).
        double shift = 0.01;

        // CG solver for (L + shift*I) * y = b
        void SolveCG(double[] b, double[] y, int maxIter = 200)
        {
            // Initialize y = b
            Array.Copy(b, y, n);
            var r = new double[n];
            var Ay = new double[n];
            LaplacianMultiply(y, Ay);
            for (int i = 0; i < n; i++)
                r[i] = b[i] - Ay[i] - shift * y[i];

            var p = (double[])r.Clone();
            double rsOld = Dot(r, r);

            for (int iter = 0; iter < maxIter; iter++)
            {
                if (rsOld < 1e-20) break;
                var Ap = new double[n];
                LaplacianMultiply(p, Ap);
                for (int i = 0; i < n; i++)
                    Ap[i] += shift * p[i];

                double pAp = Dot(p, Ap);
                if (Math.Abs(pAp) < 1e-30) break;
                double alpha = rsOld / pAp;

                for (int i = 0; i < n; i++)
                {
                    y[i] += alpha * p[i];
                    r[i] -= alpha * Ap[i];
                }

                double rsNew = Dot(r, r);
                if (rsNew < 1e-20) break;

                double beta = rsNew / rsOld;
                for (int i = 0; i < n; i++)
                    p[i] = r[i] + beta * p[i];
                rsOld = rsNew;
            }
        }

        // Start with a random vector, orthogonalized against the constant vector
        var rng = new Random(42);
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = rng.NextDouble() - 0.5;
        RemoveConstantComponent(x);
        Normalize(x);

        var y = new double[n];
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            SolveCG(x, y);
            RemoveConstantComponent(y);
            double norm = Normalize(y);
            if (norm < 1e-30) break;

            // Check convergence: |y - x| or |y + x| should be small
            double diffPlus = 0, diffMinus = 0;
            for (int i = 0; i < n; i++)
            {
                double dp = y[i] - x[i];
                double dm = y[i] + x[i];
                diffPlus += dp * dp;
                diffMinus += dm * dm;
            }
            double diff = Math.Min(diffPlus, diffMinus);

            Array.Copy(y, x, n);
            if (diff < Tolerance) break;
        }

        return x;
    }

    private static double RayleighQuotient(GraphModel graph, double[] x)
    {
        int n = graph.NodeCount;
        var Lx = new double[n];
        var degree = graph.Degree;
        var adjOff = graph.AdjOffset;
        var adjList = graph.AdjList;

        for (int i = 0; i < n; i++)
        {
            double sum = degree[i] * x[i];
            int start = adjOff[i], end = adjOff[i + 1];
            for (int j = start; j < end; j++)
                sum -= x[adjList[j]];
            Lx[i] = sum;
        }

        return Dot(x, Lx) / Dot(x, x);
    }

    private static void RemoveConstantComponent(double[] x)
    {
        double mean = 0;
        for (int i = 0; i < x.Length; i++) mean += x[i];
        mean /= x.Length;
        for (int i = 0; i < x.Length; i++) x[i] -= mean;
    }

    private static double Normalize(double[] x)
    {
        double norm = Math.Sqrt(Dot(x, x));
        if (norm > 0)
            for (int i = 0; i < x.Length; i++) x[i] /= norm;
        return norm;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
