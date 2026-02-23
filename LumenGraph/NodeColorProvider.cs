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
    private static readonly Rgba32[] CategoricalPalette =
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

    // ── Diverging gradient (blue → white → red) for in/out ratio ──
    private static readonly Rgba32 DivBlue = new(59, 130, 246);
    private static readonly Rgba32 DivWhite = new(240, 240, 240);
    private static readonly Rgba32 DivRed = new(239, 68, 68);

    // ── Neutral gray for inter-community edges ──
    private static readonly Rgba32 InterCommunityEdge = new(120, 130, 150);

    // ── Cache ──────────────────────────────────────────────────────────
    private readonly Dictionary<string, Rgba32[]> _nodeCache = new();
    private readonly Dictionary<string, Rgba32[]> _edgeCache = new();
    private GraphModel? _graph;

    /// <summary>
    /// Known node color modes.
    /// </summary>
    public static readonly string[] NodeModes = { "community", "degree", "in_out_ratio" };

    /// <summary>
    /// Known edge color modes.
    /// </summary>
    public static readonly string[] EdgeModes = { "uniform", "community" };

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
            "community" => ComputeCommunityColors(),
            "degree" => ComputeDegreeColors(),
            "in_out_ratio" => ComputeInOutRatioColors(),
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
            "community" => ComputeEdgeCommunityColors(),
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

        // Find max degree for normalization
        int maxDeg = 0;
        for (int i = 0; i < n; i++)
            if (degree[i] > maxDeg) maxDeg = degree[i];

        if (maxDeg == 0) maxDeg = 1; // avoid div-by-zero

        // Use log scale so hub structure is visible even with
        // power-law degree distributions (where a few nodes have
        // degree >> mean). log(1) = 0, log(maxDeg+1) = max.
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
        {
            // For undirected graphs, fall back to degree coloring
            return ComputeDegreeColors();
        }

        // Count in-degree and out-degree from edge arrays
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
            if (total == 0)
            {
                colors[i] = DivWhite; // isolated node
                continue;
            }

            // ratio in [-1, +1]: -1 = pure sink, +1 = pure source
            float ratio = (float)(outDeg[i] - inDeg[i]) / total;
            // Map to [0, 1] for Lerp3: 0 = blue (sink), 0.5 = white, 1 = red (source)
            float t = (ratio + 1f) / 2f;
            colors[i] = Rgba32.Lerp3(DivBlue, DivWhite, DivRed, t);
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
                // Intra-community: use community color at reduced alpha
                var c = CategoricalPalette[cs % paletteLen];
                colors[e] = new Rgba32(c.R, c.G, c.B, 80);
            }
            else
            {
                // Inter-community: neutral gray at lower alpha
                colors[e] = new Rgba32(
                    InterCommunityEdge.R,
                    InterCommunityEdge.G,
                    InterCommunityEdge.B, 35);
            }
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

    /// <summary>
    /// Sample the sequential gradient at position t in [0,1].
    /// Linearly interpolates between the defined color stops.
    /// </summary>
    private static Rgba32 SampleSequential(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int stops = SequentialStops.Length;
        float scaled = t * (stops - 1);
        int lo = Math.Min((int)scaled, stops - 2);
        float frac = scaled - lo;
        return Rgba32.Lerp(SequentialStops[lo], SequentialStops[lo + 1], frac);
    }
}
