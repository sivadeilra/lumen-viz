namespace LumenViz;

/// <summary>
/// High-performance graph data model using parallel arrays and CSR
/// (Compressed Sparse Row) adjacency. Zero per-node/per-edge heap
/// allocations — everything lives in flat arrays.
///
/// Memory layout for a graph with N nodes and M edges:
///   NodeX[N], NodeY[N]      — positions (double[])
///   Community[N]            — community assignment (int[])
///   Degree[N]               — neighbor count (int[])
///   Labels[N]               — display labels (string[], lazy)
///   EdgeSource[M]           — edge endpoints (int[])
///   EdgeTarget[M]           — edge endpoints (int[])
///   EdgeWeight[M]           — edge weights (double[])
///   AdjOffset[N+1]          — CSR row pointers (int[])
///   AdjList[2*M]            — CSR column indices (int[])
/// </summary>
public sealed class GraphModel
{
    // ── Node data (parallel arrays, indexed by node ID 0..N-1) ──────
    public int NodeCount { get; private set; }
    public double[] NodeX = Array.Empty<double>();
    public double[] NodeY = Array.Empty<double>();
    public int[] Community = Array.Empty<int>();
    public int[] Degree = Array.Empty<int>();
    public string[] Labels = Array.Empty<string>();

    // ── Edge data (parallel arrays, indexed by edge ID 0..M-1) ──────
    public int EdgeCount { get; private set; }
    public int[] EdgeSource = Array.Empty<int>();
    public int[] EdgeTarget = Array.Empty<int>();
    public double[] EdgeWeight = Array.Empty<double>();

    // ── CSR adjacency (built by BuildAdjacency) ─────────────────────
    /// <summary>AdjOffset[i]..AdjOffset[i+1] spans the neighbors of node i.</summary>
    public int[] AdjOffset = Array.Empty<int>();
    /// <summary>Flat neighbor list — use AdjOffset to slice per node.</summary>
    public int[] AdjList = Array.Empty<int>();
    /// <summary>Edge weight for each adjacency entry (parallel to AdjList).</summary>
    public double[] AdjWeight = Array.Empty<double>();

    public string Title { get; set; } = "";

    /// <summary>
    /// Get the neighbors of node i as a span. Zero allocations.
    /// </summary>
    public ReadOnlySpan<int> Neighbors(int i)
        => AdjList.AsSpan(AdjOffset[i], AdjOffset[i + 1] - AdjOffset[i]);

    // ── Builder methods (used during loading) ───────────────────────

    /// <summary>
    /// Allocate storage for N nodes with default labels "0".."N-1".
    /// </summary>
    public void AllocNodes(int count)
    {
        NodeCount = count;
        NodeX = new double[count];
        NodeY = new double[count];
        Community = new int[count];
        Degree = new int[count];
        Labels = new string[count];
        for (int i = 0; i < count; i++)
            Labels[i] = i.ToString();
    }

    /// <summary>
    /// Allocate storage for M edges.
    /// </summary>
    public void AllocEdges(int count)
    {
        EdgeCount = count;
        EdgeSource = new int[count];
        EdgeTarget = new int[count];
        EdgeWeight = new double[count];
        Array.Fill(EdgeWeight, 1.0);
    }

    /// <summary>
    /// Build CSR adjacency from edge arrays. Also computes Degree[].
    /// Call this after all edges have been set.
    /// </summary>
    public void BuildAdjacency()
    {
        int n = NodeCount;
        int m = EdgeCount;

        // Count degree of each node
        Array.Clear(Degree, 0, n);
        for (int e = 0; e < m; e++)
        {
            Degree[EdgeSource[e]]++;
            Degree[EdgeTarget[e]]++;
        }

        // Build offset array (prefix sum)
        AdjOffset = new int[n + 1];
        for (int i = 0; i < n; i++)
            AdjOffset[i + 1] = AdjOffset[i] + Degree[i];

        // Fill adjacency list + weights
        int totalAdj = AdjOffset[n]; // = 2 * M
        AdjList = new int[totalAdj];
        AdjWeight = new double[totalAdj];
        var cursor = new int[n];
        Array.Copy(AdjOffset, cursor, n);

        for (int e = 0; e < m; e++)
        {
            int s = EdgeSource[e];
            int t = EdgeTarget[e];
            double w = EdgeWeight[e];

            int cs = cursor[s];
            AdjList[cs] = t;
            AdjWeight[cs] = w;
            cursor[s] = cs + 1;

            int ct = cursor[t];
            AdjList[ct] = s;
            AdjWeight[ct] = w;
            cursor[t] = ct + 1;
        }
    }

    /// <summary>
    /// Assign communities using label propagation. Uses array-based
    /// counting to avoid Dictionary allocations per node per iteration.
    /// </summary>
    public void DetectCommunities()
    {
        int n = NodeCount;
        if (n == 0) return;

        // Initialize each node in its own community
        for (int i = 0; i < n; i++)
            Community[i] = i;

        var rng = new Random(42);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;

        // Reusable scratch arrays — zero allocation per iteration
        var labelCount = new int[n];
        var labelsSeen = new int[n];
        int labelSeenCount = 0;

        for (int iter = 0; iter < 50; iter++)
        {
            bool changed = false;
            rng.Shuffle(order);

            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                var neighbors = Neighbors(i);
                if (neighbors.Length == 0) continue;

                // Count neighbor communities using scratch arrays
                labelSeenCount = 0;
                foreach (int nb in neighbors)
                {
                    int c = Community[nb];
                    if (labelCount[c] == 0)
                        labelsSeen[labelSeenCount++] = c;
                    labelCount[c]++;
                }

                // Find the most common label
                int bestLabel = Community[i];
                int bestCount = 0;
                for (int li = 0; li < labelSeenCount; li++)
                {
                    int lbl = labelsSeen[li];
                    if (labelCount[lbl] > bestCount)
                    {
                        bestCount = labelCount[lbl];
                        bestLabel = lbl;
                    }
                }

                // Clear only the slots we used
                for (int li = 0; li < labelSeenCount; li++)
                    labelCount[labelsSeen[li]] = 0;

                if (bestLabel != Community[i])
                {
                    Community[i] = bestLabel;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        // Renumber communities to 0..K-1 using an array (no Dictionary)
        var communityMap = new int[n];
        Array.Fill(communityMap, -1);
        int nextId = 0;
        for (int i = 0; i < n; i++)
        {
            int c = Community[i];
            if (communityMap[c] < 0)
                communityMap[c] = nextId++;
            Community[i] = communityMap[c];
        }
    }
}
