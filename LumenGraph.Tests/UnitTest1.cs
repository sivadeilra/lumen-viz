using LumenGraph.Partition;

namespace LumenGraph.Tests;

/// <summary>
/// Shared helpers for building test graphs and verifying partition invariants.
/// </summary>
internal static class TestGraphs
{
    /// <summary>Build a small triangle graph: 3 nodes, 3 edges.</summary>
    public static GraphModel Triangle()
    {
        var g = new GraphModel { Title = "Triangle" };
        g.AllocNodes(3);
        g.AllocEdges(3);
        g.EdgeSource[0] = 0; g.EdgeTarget[0] = 1; g.EdgeWeight[0] = 1;
        g.EdgeSource[1] = 1; g.EdgeTarget[1] = 2; g.EdgeWeight[1] = 1;
        g.EdgeSource[2] = 0; g.EdgeTarget[2] = 2; g.EdgeWeight[2] = 1;
        g.BuildAdjacency();
        return g;
    }

    /// <summary>Build a path graph: 0-1-2-..-(n-1).</summary>
    public static GraphModel Path(int n)
    {
        var g = new GraphModel { Title = $"Path({n})" };
        g.AllocNodes(n);
        g.AllocEdges(n - 1);
        for (int i = 0; i < n - 1; i++)
        {
            g.EdgeSource[i] = i;
            g.EdgeTarget[i] = i + 1;
            g.EdgeWeight[i] = 1;
        }
        g.BuildAdjacency();
        return g;
    }

    /// <summary>Build a barbell: two complete K_n cliques connected by a single bridge edge.</summary>
    public static GraphModel Barbell(int cliqueSize)
    {
        int n = 2 * cliqueSize;
        var edges = new List<(int, int, double)>();
        // Clique 1: nodes 0..cliqueSize-1
        for (int i = 0; i < cliqueSize; i++)
            for (int j = i + 1; j < cliqueSize; j++)
                edges.Add((i, j, 1));
        // Clique 2: nodes cliqueSize..2*cliqueSize-1
        for (int i = cliqueSize; i < n; i++)
            for (int j = i + 1; j < n; j++)
                edges.Add((i, j, 1));
        // Bridge
        edges.Add((cliqueSize - 1, cliqueSize, 1));

        var g = new GraphModel { Title = $"Barbell({cliqueSize})" };
        g.AllocNodes(n);
        g.AllocEdges(edges.Count);
        for (int e = 0; e < edges.Count; e++)
        {
            g.EdgeSource[e] = edges[e].Item1;
            g.EdgeTarget[e] = edges[e].Item2;
            g.EdgeWeight[e] = edges[e].Item3;
        }
        g.BuildAdjacency();
        return g;
    }

    /// <summary>
    /// Verify all structural invariants of a bisection result.
    /// Returns (passed, messages).
    /// </summary>
    public static void AssertValidBisection(
        GraphModel graph, PartitionResult result, PartitionOptions? options = null)
    {
        options ??= new PartitionOptions();
        int n = graph.NodeCount;

        Assert.Equal(n, result.Side.Length);
        Assert.Equal(n, result.SizeA + result.SizeB);

        if (n > 1)
        {
            Assert.True(result.SizeA > 0, "Partition A is empty");
            Assert.True(result.SizeB > 0, "Partition B is empty");
        }

        Assert.True(result.EdgeCut >= 0, $"Negative edge cut: {result.EdgeCut}");

        // Verify edge cut independently
        double recomputedCut = 0;
        for (int e = 0; e < graph.EdgeCount; e++)
            if (result.Side[graph.EdgeSource[e]] != result.Side[graph.EdgeTarget[e]])
                recomputedCut += graph.EdgeWeight[e];
        Assert.Equal(recomputedCut, result.EdgeCut, precision: 6);

        // Verify size counts
        int countA = 0, countB = 0;
        for (int i = 0; i < n; i++)
        {
            if (result.Side[i]) countB++;
            else countA++;
        }
        Assert.Equal(countA, result.SizeA);
        Assert.Equal(countB, result.SizeB);

        // Check balance
        double maxImbalance = 1.0 + options.BalanceTolerance;
        Assert.True(result.Imbalance <= maxImbalance + 1e-9,
            $"Imbalance {result.Imbalance:F4} exceeds {maxImbalance:F4}");
    }

    /// <summary>
    /// Verify all structural invariants of a k-way partition.
    /// </summary>
    public static void AssertValidKWay(GraphModel graph, int[] partId, int k)
    {
        Assert.Equal(graph.NodeCount, partId.Length);

        var sizes = new Dictionary<int, int>();
        for (int i = 0; i < partId.Length; i++)
        {
            Assert.InRange(partId[i], 0, k - 1);
            sizes.TryGetValue(partId[i], out int c);
            sizes[partId[i]] = c + 1;
        }

        Assert.True(sizes.Count <= k, $"Got {sizes.Count} parts, expected ≤{k}");
    }
}
