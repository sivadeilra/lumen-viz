namespace LumenGraph;

/// <summary>
/// Computes and caches per-node and per-edge color arrays for different
/// visualization modes. Each mode produces an <see cref="Rgba32"/>[]
/// parallel to the graph's node or edge arrays. Arrays are computed
/// on-demand and cached until <see cref="Invalidate"/> is called
/// (typically when the graph changes).
/// </summary>
public sealed class NodeColorProvider
{
    // ── Categorical palette (matches the 12-color palette in GraphView) ──
    public static readonly Rgba32[] CategoricalPalette =
    {
        new(102, 194, 255),
        new(255, 128, 102),
        new(128, 230, 128),
        new(255, 204, 77),
        new(204, 153, 255),
        new(255, 153, 204),
        new(102, 230, 204),
        new(255, 179, 102),
        new(153, 204, 255),
        new(230, 230, 128),
        new(204, 128, 255),
        new(128, 204, 179),
    };

    // ── Sequential gradient (Viridis-inspired: dark purple → teal → yellow) ──
    private static readonly Rgba32[] SequentialStops =
    {
        new( 68,   1, 84),   // 0.00 - dark purple
        new( 59,  82, 139),  // 0.25 - blue
        new( 33, 145, 140),  // 0.50 - teal
        new( 94, 201,  98),  // 0.75 - green
        new(253, 231,  37),  // 1.00 - yellow
    };

    // ── Heat gradient for betweenness/pagerank (black → red → orange → yellow → white) ──
    private static readonly Rgba32[] HeatStops =
    {
        new( 10,  10,  20),  // 0.00 - near-black
        new(180,  30,  30),  // 0.33 - red
        new(240, 160,  30),  // 0.66 - orange
        new(255, 255, 200),  // 1.00 - hot white
    };

    // ── Diverging gradient (blue → white → red) for in/out ratio ──
    private static readonly Rgba32 DivBlue = new(59, 130, 246);
    private static readonly Rgba32 DivWhite = new(240, 240, 240);
    private static readonly Rgba32 DivRed = new(239, 68, 68);

    // ── Neutral gray for inter-community / uniform edges ──
    private static readonly Rgba32 InterCommunityEdge = new(120, 130, 150);

    // ── Accent colors for edge modes ──
    private static readonly Rgba32 MutualEdge = new(60, 180, 80, 120);     // green
    private static readonly Rgba32 OneWayEdge = new(220, 80, 60, 90);      // red
    private static readonly Rgba32 BridgeEdge = new(255, 200, 40, 200);    // bright gold

    // ── Cache ──────────────────────────────────────────────────────────
    private readonly Dictionary<string, Rgba32[]> _nodeCache = new();
    private readonly Dictionary<string, Rgba32[]> _edgeCache = new();
    private GraphModel? _graph;

    /// <summary>Known node color modes.</summary>
    public static readonly string[] NodeModes =
    {
        "community", "degree", "in_out_ratio",
        "betweenness", "pagerank", "clustering", "kcore",
    };

    /// <summary>Known edge color modes.</summary>
    public static readonly string[] EdgeModes =
    {
        "uniform", "community", "weight", "reciprocity", "bridge",
    };

    /// <summary>
    /// Bind to a (possibly new) graph and invalidate all caches.
    /// </summary>
    public void SetGraph(GraphModel? graph)
    {
        _graph = graph;
        Invalidate();
    }

    /// <summary>
    /// Clear all cached color arrays. Call when graph data changes
    /// (e.g., community re-detection, level change).
    /// </summary>
    public void Invalidate()
    {
        _nodeCache.Clear();
        _edgeCache.Clear();
    }

    /// <summary>
    /// Get the per-node color array for the given mode.
    /// Computed on first request, then cached.
    /// </summary>
    public Rgba32[] GetNodeColors(string mode)
    {
        if (_graph == null || _graph.NodeCount == 0)
            return Array.Empty<Rgba32>();

        if (_nodeCache.TryGetValue(mode, out var cached))
            return cached;

        var colors = mode switch
        {
            "community"    => ComputeCommunityColors(),
            "degree"       => ComputeDegreeColors(),
            "in_out_ratio" => ComputeInOutRatioColors(),
            "betweenness"  => ComputeBetweennessColors(),
            "pagerank"     => ComputePageRankColors(),
            "clustering"   => ComputeClusteringColors(),
            "kcore"        => ComputeKCoreColors(),
            _ => ComputeCommunityColors(), // fallback
        };

        _nodeCache[mode] = colors;
        return colors;
    }

    /// <summary>
    /// Get the per-edge color array for the given mode.
    /// Computed on first request, then cached.
    /// </summary>
    public Rgba32[] GetEdgeColors(string mode)
    {
        if (_graph == null || _graph.EdgeCount == 0)
            return Array.Empty<Rgba32>();

        if (_edgeCache.TryGetValue(mode, out var cached))
            return cached;

        var colors = mode switch
        {
            "community"   => ComputeEdgeCommunityColors(),
            "weight"      => ComputeEdgeWeightColors(),
            "reciprocity" => ComputeEdgeReciprocityColors(),
            "bridge"      => ComputeEdgeBridgeColors(),
            _ => ComputeUniformEdgeColors(),
        };

        _edgeCache[mode] = colors;
        return colors;
    }

    // ════════════════════════════════════════════════════════════════════
    // Node color computations
    // ════════════════════════════════════════════════════════════════════

    private Rgba32[] ComputeCommunityColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var colors = new Rgba32[n];
        var community = g.Community;
        int paletteLen = CategoricalPalette.Length;

        for (int i = 0; i < n; i++)
            colors[i] = CategoricalPalette[community[i] % paletteLen];

        return colors;
    }

    private Rgba32[] ComputeDegreeColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var colors = new Rgba32[n];
        var degree = g.Degree;

        int maxDeg = 0;
        for (int i = 0; i < n; i++)
            if (degree[i] > maxDeg) maxDeg = degree[i];
        if (maxDeg == 0) maxDeg = 1;

        double logMax = Math.Log(maxDeg + 1);
        for (int i = 0; i < n; i++)
        {
            float t = (float)(Math.Log(degree[i] + 1) / logMax);
            colors[i] = SampleSequential(t);
        }
        return colors;
    }

    private Rgba32[] ComputeInOutRatioColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var colors = new Rgba32[n];

        if (!g.IsDirected)
            return ComputeDegreeColors();

        var inDeg = new int[n];
        var outDeg = new int[n];
        var es = g.EdgeSource;
        var et = g.EdgeTarget;
        int m = g.EdgeCount;
        for (int e = 0; e < m; e++)
        {
            outDeg[es[e]]++;
            inDeg[et[e]]++;
        }

        for (int i = 0; i < n; i++)
        {
            int total = inDeg[i] + outDeg[i];
            if (total == 0) { colors[i] = DivWhite; continue; }
            float ratio = (float)(outDeg[i] - inDeg[i]) / total;
            float t = (ratio + 1f) / 2f;
            colors[i] = Rgba32.Lerp3(DivBlue, DivWhite, DivRed, t);
        }
        return colors;
    }

    /// <summary>
    /// Betweenness centrality: fraction of all-pairs shortest paths passing
    /// through each node. Brandes' algorithm, O(n·m). For large graphs
    /// (&gt;5000 nodes) we sample 500 sources to keep it interactive.
    /// </summary>
    private Rgba32[] ComputeBetweennessColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var bc = new double[n];

        // Brandes' algorithm (unweighted BFS variant)
        int sampleLimit = n > 5000 ? 500 : n;
        int step = Math.Max(1, n / sampleLimit);

        var stack = new List<int>(n);
        var pred = new List<int>[n];
        for (int i = 0; i < n; i++) pred[i] = new List<int>();
        var sigma = new double[n];
        var dist = new int[n];
        var delta = new double[n];
        var queue = new Queue<int>(n);

        for (int s = 0; s < n; s += step)
        {
            stack.Clear();
            for (int i = 0; i < n; i++) { pred[i].Clear(); sigma[i] = 0; dist[i] = -1; delta[i] = 0; }
            sigma[s] = 1; dist[s] = 0;
            queue.Clear(); queue.Enqueue(s);

            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                stack.Add(v);
                var neighbors = g.Neighbors(v);
                for (int ni = 0; ni < neighbors.Length; ni++)
                {
                    int w = neighbors[ni];
                    if (dist[w] < 0) { dist[w] = dist[v] + 1; queue.Enqueue(w); }
                    if (dist[w] == dist[v] + 1) { sigma[w] += sigma[v]; pred[w].Add(v); }
                }
            }

            for (int i = stack.Count - 1; i >= 0; i--)
            {
                int w = stack[i];
                foreach (int v in pred[w])
                    delta[v] += (sigma[v] / sigma[w]) * (1.0 + delta[w]);
                if (w != s) bc[w] += delta[w];
            }
        }

        // Normalize and map to heat gradient
        double maxBc = 0;
        for (int i = 0; i < n; i++) if (bc[i] > maxBc) maxBc = bc[i];
        if (maxBc == 0) maxBc = 1;

        var colors = new Rgba32[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)Math.Sqrt(bc[i] / maxBc); // sqrt scale to spread distribution
            colors[i] = SampleHeat(t);
        }
        return colors;
    }

    /// <summary>
    /// PageRank: iterative random-walk importance measure.
    /// 20 iterations with damping factor 0.85.
    /// Falls back to degree for undirected graphs.
    /// </summary>
    private Rgba32[] ComputePageRankColors()
    {
        var g = _graph!;
        int n = g.NodeCount;

        if (!g.IsDirected)
            return ComputeDegreeColors();

        // Build out-degree from edge arrays
        var outDeg = new int[n];
        var es = g.EdgeSource;
        var et = g.EdgeTarget;
        int m = g.EdgeCount;
        for (int e = 0; e < m; e++)
            outDeg[es[e]]++;

        const double d = 0.85;
        double init = 1.0 / n;
        var rank = new double[n];
        var next = new double[n];
        Array.Fill(rank, init);

        for (int iter = 0; iter < 20; iter++)
        {
            double leak = 0; // dangling node mass
            for (int i = 0; i < n; i++)
                if (outDeg[i] == 0) leak += rank[i];

            double base_val = (1.0 - d + d * leak) / n;
            Array.Fill(next, base_val);

            for (int e = 0; e < m; e++)
            {
                int src = es[e];
                next[et[e]] += d * rank[src] / outDeg[src];
            }

            // Swap
            (rank, next) = (next, rank);
        }

        // Normalize and map to heat gradient
        double maxR = 0;
        for (int i = 0; i < n; i++) if (rank[i] > maxR) maxR = rank[i];
        if (maxR == 0) maxR = 1;

        var colors = new Rgba32[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)Math.Sqrt(rank[i] / maxR); // sqrt to spread
            colors[i] = SampleHeat(t);
        }
        return colors;
    }

    /// <summary>
    /// Local clustering coefficient: for each node, what fraction of its
    /// neighbor pairs are also connected. High = tightly-knit clique,
    /// low = star/bridge topology.
    /// </summary>
    private Rgba32[] ComputeClusteringColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var cc = new float[n];

        for (int i = 0; i < n; i++)
        {
            var nb = g.Neighbors(i);
            int k = nb.Length;
            if (k < 2) { cc[i] = 0; continue; }

            // Count edges among neighbors using adjacency lookup.
            // For each pair (u,v) in nb, check if v is in u's neighbor list.
            int triangles = 0;
            for (int a = 0; a < k; a++)
            {
                int u = nb[a];
                var uNb = g.Neighbors(u);
                for (int b = a + 1; b < k; b++)
                {
                    int v = nb[b];
                    // Binary search or linear scan in u's neighbors
                    // CSR adjacency is sorted? Use linear scan (safe).
                    for (int c = 0; c < uNb.Length; c++)
                    {
                        if (uNb[c] == v) { triangles++; break; }
                    }
                }
            }
            cc[i] = (float)(2.0 * triangles / (k * (k - 1)));
        }

        // Map to sequential gradient: 0 = dark (low clustering), 1 = bright (clique)
        var colors = new Rgba32[n];
        for (int i = 0; i < n; i++)
            colors[i] = SampleSequential(cc[i]);
        return colors;
    }

    /// <summary>
    /// K-core decomposition: assigns each node to the highest k such that
    /// the node belongs to a subgraph where every node has degree ≥ k.
    /// O(n+m) using iterative peeling.
    /// </summary>
    private Rgba32[] ComputeKCoreColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        var deg = new int[n];
        for (int i = 0; i < n; i++) deg[i] = g.Degree[i];

        int maxDeg = 0;
        for (int i = 0; i < n; i++) if (deg[i] > maxDeg) maxDeg = deg[i];
        if (maxDeg == 0) maxDeg = 1;

        // Bucket sort by degree
        var bin = new int[maxDeg + 1];
        for (int i = 0; i < n; i++) bin[deg[i]]++;
        int start = 0;
        for (int d = 0; d <= maxDeg; d++) { int tmp = bin[d]; bin[d] = start; start += tmp; }

        var order = new int[n];
        var pos = new int[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = bin[deg[i]];
            order[pos[i]] = i;
            bin[deg[i]]++;
        }

        // Restore bin starts
        for (int d = maxDeg; d > 0; d--) bin[d] = bin[d - 1];
        bin[0] = 0;

        var core = new int[n];
        for (int i = 0; i < n; i++)
        {
            int v = order[i];
            core[v] = deg[v];
            var nb = g.Neighbors(v);
            for (int j = 0; j < nb.Length; j++)
            {
                int u = nb[j];
                if (deg[u] > deg[v])
                {
                    // Swap u to one position earlier in its bin
                    int du = deg[u];
                    int pu = pos[u];
                    int pw = bin[du];
                    int w = order[pw];
                    // Swap positions
                    order[pu] = w; order[pw] = u;
                    pos[u] = pw; pos[w] = pu;
                    bin[du]++;
                    deg[u]--;
                }
            }
        }

        // Map core number to sequential gradient
        int maxCore = 0;
        for (int i = 0; i < n; i++) if (core[i] > maxCore) maxCore = core[i];
        if (maxCore == 0) maxCore = 1;

        var colors = new Rgba32[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)core[i] / maxCore;
            colors[i] = SampleSequential(t);
        }
        return colors;
    }

    // ════════════════════════════════════════════════════════════════════
    // Edge color computations
    // ════════════════════════════════════════════════════════════════════

    private Rgba32[] ComputeEdgeCommunityColors()
    {
        var g = _graph!;
        int m = g.EdgeCount;
        var colors = new Rgba32[m];
        var es = g.EdgeSource;
        var et = g.EdgeTarget;
        var community = g.Community;
        int paletteLen = CategoricalPalette.Length;

        for (int e = 0; e < m; e++)
        {
            int cs = community[es[e]];
            int ct = community[et[e]];

            if (cs == ct)
            {
                var c = CategoricalPalette[cs % paletteLen];
                colors[e] = new Rgba32(c.R, c.G, c.B, 80);
            }
            else
            {
                colors[e] = new Rgba32(InterCommunityEdge.R, InterCommunityEdge.G,
                    InterCommunityEdge.B, 35);
            }
        }
        return colors;
    }

    /// <summary>
    /// Edge weight mapped to sequential gradient. Thicker/brighter = heavier.
    /// Falls back to uniform if no weights present.
    /// </summary>
    private Rgba32[] ComputeEdgeWeightColors()
    {
        var g = _graph!;
        int m = g.EdgeCount;
        var colors = new Rgba32[m];
        var w = g.EdgeWeight;

        if (w == null || w.Length == 0)
            return ComputeUniformEdgeColors();

        double minW = double.MaxValue, maxW = double.MinValue;
        for (int e = 0; e < m; e++)
        {
            if (w[e] < minW) minW = w[e];
            if (w[e] > maxW) maxW = w[e];
        }
        double range = maxW - minW;
        if (range < 1e-12) range = 1;

        for (int e = 0; e < m; e++)
        {
            float t = (float)((w[e] - minW) / range);
            var c = SampleSequential(t);
            // Brighter = heavier, with alpha from 40 (light) to 180 (heavy)
            byte a = (byte)(40 + (int)(140 * t));
            colors[e] = new Rgba32(c.R, c.G, c.B, a);
        }
        return colors;
    }

    /// <summary>
    /// Reciprocity: for directed graphs, mutual edges (A→B and B→A both
    /// exist) shown in green, one-way edges in red.
    /// Falls back to uniform for undirected graphs.
    /// </summary>
    private Rgba32[] ComputeEdgeReciprocityColors()
    {
        var g = _graph!;
        int m = g.EdgeCount;
        var colors = new Rgba32[m];

        if (!g.IsDirected)
            return ComputeUniformEdgeColors();

        // Build a set of directed edges for O(1) reciprocal lookup
        var edgeSet = new HashSet<long>(m);
        var es = g.EdgeSource;
        var et = g.EdgeTarget;
        for (int e = 0; e < m; e++)
            edgeSet.Add(EdgeKey(es[e], et[e]));

        for (int e = 0; e < m; e++)
        {
            bool mutual = edgeSet.Contains(EdgeKey(et[e], es[e]));
            colors[e] = mutual ? MutualEdge : OneWayEdge;
        }
        return colors;
    }

    /// <summary>
    /// Bridge edges: edges whose removal increases the number of connected
    /// components. Found via Tarjan's bridge-finding DFS in O(n+m).
    /// Bridges shown in bright gold, others in dim gray.
    /// </summary>
    private Rgba32[] ComputeEdgeBridgeColors()
    {
        var g = _graph!;
        int n = g.NodeCount;
        int m = g.EdgeCount;
        var colors = new Rgba32[m];
        Array.Fill(colors, new Rgba32(InterCommunityEdge.R, InterCommunityEdge.G,
            InterCommunityEdge.B, 50));

        // Build edge index lookup: for each adjacency entry, store the edge index
        // We need to identify which edges are bridges.
        // Use Tarjan's bridge algorithm on the CSR adjacency.
        var disc = new int[n];
        var low = new int[n];
        var visited = new bool[n];
        Array.Fill(disc, -1);
        int timer = 0;

        // Bridge set: store (min(u,v), max(u,v)) pairs
        var bridges = new HashSet<long>();

        // Iterative DFS to avoid stack overflow on large graphs
        var dfsStack = new Stack<(int node, int parent, int neighborIdx)>();

        for (int startNode = 0; startNode < n; startNode++)
        {
            if (visited[startNode]) continue;

            disc[startNode] = low[startNode] = timer++;
            visited[startNode] = true;
            dfsStack.Push((startNode, -1, g.AdjOffset[startNode]));

            while (dfsStack.Count > 0)
            {
                var (u, parent, ni) = dfsStack.Pop();
                int end = g.AdjOffset[u + 1];

                bool pushed = false;
                for (int idx = ni; idx < end; idx++)
                {
                    int v = g.AdjList[idx];
                    if (!visited[v])
                    {
                        visited[v] = true;
                        disc[v] = low[v] = timer++;
                        // Push current state back (resume at idx+1 after child returns)
                        dfsStack.Push((u, parent, idx + 1));
                        dfsStack.Push((v, u, g.AdjOffset[v]));
                        pushed = true;
                        break;
                    }
                    else if (v != parent)
                    {
                        low[u] = Math.Min(low[u], disc[v]);
                    }
                }

                if (!pushed && dfsStack.Count > 0)
                {
                    // Child returned — update parent's low value
                    var (pu, _, _) = dfsStack.Peek();
                    low[pu] = Math.Min(low[pu], low[u]);
                    if (low[u] > disc[pu])
                    {
                        int a = Math.Min(pu, u), b = Math.Max(pu, u);
                        bridges.Add((long)a << 32 | (uint)b);
                    }
                }
            }
        }

        // Color edges
        var es = g.EdgeSource;
        var et = g.EdgeTarget;
        for (int e = 0; e < m; e++)
        {
            int a = Math.Min(es[e], et[e]), b = Math.Max(es[e], et[e]);
            if (bridges.Contains((long)a << 32 | (uint)b))
                colors[e] = BridgeEdge;
        }
        return colors;
    }

    private Rgba32[] ComputeUniformEdgeColors()
    {
        var g = _graph!;
        int m = g.EdgeCount;
        var colors = new Rgba32[m];
        Array.Fill(colors, InterCommunityEdge);
        return colors;
    }

    // ════════════════════════════════════════════════════════════════════
    // Gradient helpers
    // ════════════════════════════════════════════════════════════════════

    private static long EdgeKey(int src, int tgt) => (long)src << 32 | (uint)tgt;

    /// <summary>
    /// Sample the sequential (Viridis) gradient at position t in [0,1].
    /// </summary>
    public static Rgba32 SampleSequential(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int stops = SequentialStops.Length;
        float scaled = t * (stops - 1);
        int lo = Math.Min((int)scaled, stops - 2);
        float frac = scaled - lo;
        return Rgba32.Lerp(SequentialStops[lo], SequentialStops[lo + 1], frac);
    }

    /// <summary>
    /// Sample the heat (black→red→orange→white) gradient at position t in [0,1].
    /// </summary>
    public static Rgba32 SampleHeat(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int stops = HeatStops.Length;
        float scaled = t * (stops - 1);
        int lo = Math.Min((int)scaled, stops - 2);
        float frac = scaled - lo;
        return Rgba32.Lerp(HeatStops[lo], HeatStops[lo + 1], frac);
    }
}
