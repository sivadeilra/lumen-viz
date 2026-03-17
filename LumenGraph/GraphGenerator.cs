namespace LumenGraph;

/// <summary>
/// Classic random graph generators. Each returns a fully-built GraphModel
/// with CSR adjacency, unit edge weights, and default sequential labels.
///
///   Erdős-Rényi:       G(n, p) — each possible edge exists independently with probability p.
///   Barabási-Albert:   Scale-free via preferential attachment — power-law degree distribution.
///   Watts-Strogatz:    Small-world — regular ring lattice with random rewiring.
///   Complete:          K_n — all possible edges.
///   Ring Lattice:      Each node connected to its k nearest neighbors on a ring.
///   Star:              Central hub connected to all other nodes.
/// </summary>
public static class GraphGenerator
{
    /// <summary>
    /// Erdős-Rényi random graph G(n, p). Each of the n*(n-1)/2 possible
    /// undirected edges is included independently with probability p.
    ///
    /// Expected edge count: p * n*(n-1)/2.
    /// For sparse random graphs, use p ~ c/n where c is the desired average degree.
    /// </summary>
    public static GraphModel ErdosRenyi(int n, double p, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        p = Math.Clamp(p, 0, 1);

        var rng = new Random(seed);
        var edges = new List<(int s, int t)>();

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (rng.NextDouble() < p)
                    edges.Add((i, j));

        return BuildGraph(n, edges, $"Erdős-Rényi G({n}, {p:F3})");
    }

    /// <summary>
    /// Barabási-Albert preferential attachment model. Starts with a fully
    /// connected seed graph of m₀ nodes, then adds nodes one at a time,
    /// each connecting to m existing nodes with probability proportional
    /// to their current degree.
    ///
    /// Produces a scale-free graph with power-law degree distribution P(k) ~ k^{-3}.
    /// </summary>
    /// <param name="n">Total number of nodes (must be > m).</param>
    /// <param name="m">Number of edges each new node adds (attachment count).</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    public static GraphModel BarabasiAlbert(int n, int m, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m);
        if (m >= n)
            throw new ArgumentException($"m ({m}) must be less than n ({n}).");

        var rng = new Random(seed);
        var edges = new List<(int s, int t)>();

        // Seed: fully-connected graph of (m+1) nodes
        int m0 = m + 1;
        for (int i = 0; i < m0; i++)
            for (int j = i + 1; j < m0; j++)
                edges.Add((i, j));

        // Degree tracking for preferential attachment
        var degree = new int[n];
        for (int i = 0; i < m0; i++)
            degree[i] = m0 - 1;
        int totalDegree = m0 * (m0 - 1);

        // Add remaining nodes
        for (int v = m0; v < n; v++)
        {
            var targets = new HashSet<int>();
            int attempts = 0;
            while (targets.Count < m && attempts < m * 100)
            {
                attempts++;
                // Select target proportional to degree
                int r = rng.Next(totalDegree);
                int cumulative = 0;
                for (int i = 0; i < v; i++)
                {
                    cumulative += degree[i];
                    if (cumulative > r)
                    {
                        targets.Add(i);
                        break;
                    }
                }
            }

            foreach (int t in targets)
            {
                edges.Add((v, t));
                degree[v]++;
                degree[t]++;
                totalDegree += 2;
            }
        }

        return BuildGraph(n, edges, $"Barabási-Albert BA({n}, {m})");
    }

    /// <summary>
    /// Watts-Strogatz small-world model. Starts with a ring lattice where
    /// each node is connected to its k nearest neighbors, then rewires each
    /// edge with probability β — creating shortcuts that drastically reduce
    /// average path length while maintaining high clustering.
    ///
    /// β=0: regular ring lattice
    /// β=1: fully random (like Erdős-Rényi but with same edge count)
    /// β~0.01–0.1: "small world" regime
    /// </summary>
    /// <param name="n">Number of nodes (must be > k).</param>
    /// <param name="k">Each node connects to k/2 neighbors on each side. Must be even.</param>
    /// <param name="beta">Rewiring probability [0, 1].</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    public static GraphModel WattsStrogatz(int n, int k, double beta, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        if (k < 2 || k >= n)
            throw new ArgumentException($"k ({k}) must be >= 2 and < n ({n}).");
        if (k % 2 != 0)
            throw new ArgumentException($"k ({k}) must be even.");
        beta = Math.Clamp(beta, 0, 1);

        var rng = new Random(seed);

        // Build initial ring lattice edges (as a set for O(1) lookup during rewiring)
        var edgeSet = new HashSet<(int, int)>();
        for (int i = 0; i < n; i++)
            for (int j = 1; j <= k / 2; j++)
            {
                int neighbor = (i + j) % n;
                int lo = Math.Min(i, neighbor);
                int hi = Math.Max(i, neighbor);
                edgeSet.Add((lo, hi));
            }

        // Rewire
        var edgeList = edgeSet.ToList();
        for (int e = 0; e < edgeList.Count; e++)
        {
            if (rng.NextDouble() >= beta) continue;

            var (u, v) = edgeList[e];
            edgeSet.Remove((u, v));

            // Pick a new target for u
            int newV = u;
            int attempts = 0;
            while (attempts < n * 2)
            {
                newV = rng.Next(n);
                if (newV != u)
                {
                    int lo = Math.Min(u, newV);
                    int hi = Math.Max(u, newV);
                    if (!edgeSet.Contains((lo, hi)))
                    {
                        edgeSet.Add((lo, hi));
                        edgeList[e] = (lo, hi);
                        break;
                    }
                }
                attempts++;
            }

            if (attempts >= n * 2)
            {
                // Could not find a valid rewire target; restore original
                edgeSet.Add((u, v));
            }
        }

        var edges = edgeSet.Select(e => (e.Item1, e.Item2)).ToList();
        return BuildGraph(n, edges, $"Watts-Strogatz WS({n}, {k}, {beta:F2})");
    }

    /// <summary>
    /// Complete graph K_n — every pair of nodes is connected.
    /// Edge count: n*(n-1)/2.
    /// </summary>
    public static GraphModel Complete(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        var edges = new List<(int s, int t)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                edges.Add((i, j));
        return BuildGraph(n, edges, $"Complete K_{n}");
    }

    /// <summary>
    /// Star graph — one central hub (node 0) connected to all other n-1 nodes.
    /// </summary>
    public static GraphModel Star(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        var edges = new List<(int s, int t)>();
        for (int i = 1; i < n; i++)
            edges.Add((0, i));
        return BuildGraph(n, edges, $"Star S_{n}");
    }

    /// <summary>
    /// Ring lattice — each node connected to its k nearest neighbors on a ring.
    /// This is the starting point for Watts-Strogatz (before rewiring).
    /// </summary>
    public static GraphModel RingLattice(int n, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        if (k < 2 || k >= n)
            throw new ArgumentException($"k ({k}) must be >= 2 and < n ({n}).");
        if (k % 2 != 0)
            throw new ArgumentException($"k ({k}) must be even.");

        var edgeSet = new HashSet<(int, int)>();
        for (int i = 0; i < n; i++)
            for (int j = 1; j <= k / 2; j++)
            {
                int neighbor = (i + j) % n;
                int lo = Math.Min(i, neighbor);
                int hi = Math.Max(i, neighbor);
                edgeSet.Add((lo, hi));
            }

        var edges = edgeSet.Select(e => (e.Item1, e.Item2)).ToList();
        return BuildGraph(n, edges, $"Ring Lattice ({n}, {k})");
    }

    // ════════════════════════════════════════════════════════════════════

    private static GraphModel BuildGraph(int n, List<(int s, int t)> edges, string title)
    {
        var graph = new GraphModel();
        graph.AllocNodes(n);
        graph.AllocEdges(edges.Count);

        for (int i = 0; i < edges.Count; i++)
        {
            graph.EdgeSource[i] = edges[i].s;
            graph.EdgeTarget[i] = edges[i].t;
        }

        graph.BuildAdjacency();
        graph.DetectCommunities();
        graph.Title = title;
        return graph;
    }
}
