using LumenGraph.Partition;

namespace LumenGraph.Tests;

public class EdgeSeparatorTests
{
    // ── Trivial cases ──────────────────────────────────────────

    [Fact]
    public void EmptyGraph_ReturnsEmptyResult()
    {
        var g = new GraphModel { Title = "Empty" };
        g.AllocNodes(0);
        g.AllocEdges(0);
        g.BuildAdjacency();

        var result = EdgeSeparator.Bisect(g);
        Assert.Equal(0, result.N);
    }

    [Fact]
    public void SingleNode_ReturnsOneSide()
    {
        var g = new GraphModel { Title = "Single" };
        g.AllocNodes(1);
        g.AllocEdges(0);
        g.BuildAdjacency();

        var result = EdgeSeparator.Bisect(g);
        Assert.Equal(1, result.N);
    }

    [Fact]
    public void TwoNodes_PerfectBisection()
    {
        var g = new GraphModel { Title = "Edge" };
        g.AllocNodes(2);
        g.AllocEdges(1);
        g.EdgeSource[0] = 0;
        g.EdgeTarget[0] = 1;
        g.EdgeWeight[0] = 1;
        g.BuildAdjacency();

        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
        Assert.Equal(1, result.SizeA);
        Assert.Equal(1, result.SizeB);
        Assert.Equal(1.0, result.EdgeCut);
    }

    // ── Small structured graphs ────────────────────────────────

    [Fact]
    public void Triangle_Bisection()
    {
        var g = TestGraphs.Triangle();
        // 3 nodes can't be split better than 2/1 = imbalance 1.333
        var opts = new PartitionOptions { BalanceTolerance = 0.4 };
        var result = EdgeSeparator.Bisect(g, opts);
        TestGraphs.AssertValidBisection(g, result, opts);
    }

    [Fact]
    public void Path10_Bisection()
    {
        var g = TestGraphs.Path(10);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
        // Optimal cut on a path of 10 = 1 (cut at the middle)
        Assert.Equal(1.0, result.EdgeCut);
    }

    [Fact]
    public void Barbell_FindsBridgeCut()
    {
        // Two K_10 cliques connected by a single bridge.
        // Optimal bisection cut = 1 (the bridge).
        var g = TestGraphs.Barbell(10);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
        Assert.Equal(1.0, result.EdgeCut);
    }

    [Fact]
    public void Complete20_Bisection()
    {
        var g = GraphGenerator.Complete(20);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
        // K_20 bisected at 10/10: cut = 100, at 9/11: cut = 99, etc.
        Assert.InRange(result.EdgeCut, 90.0, 110.0);
    }

    [Fact]
    public void Star_Bisection()
    {
        var g = GraphGenerator.Star(21); // hub + 20 leaves
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    [Fact]
    public void RingLattice_Bisection()
    {
        var g = GraphGenerator.RingLattice(50, 4); // 50 nodes, each connected to 4 nearest
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    // ── Random graphs ──────────────────────────────────────────

    [Theory]
    [InlineData(50, 3)]
    [InlineData(100, 5)]
    [InlineData(200, 3)]
    [InlineData(500, 4)]
    public void BarabasiAlbert_Bisection(int n, int m)
    {
        var g = GraphGenerator.BarabasiAlbert(n, m, seed: 42);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    [Theory]
    [InlineData(50, 0.1)]
    [InlineData(100, 0.05)]
    [InlineData(200, 0.05)]
    public void ErdosRenyi_Bisection(int n, double p)
    {
        var g = GraphGenerator.ErdosRenyi(n, p, seed: 42);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    [Fact]
    public void WattsStrogatz_Bisection()
    {
        var g = GraphGenerator.WattsStrogatz(100, 6, 0.3, seed: 42);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    // ── Matching strategy sweep ────────────────────────────────

    [Theory]
    [InlineData(MatchingStrategy.Random)]
    [InlineData(MatchingStrategy.HEM)]
    [InlineData(MatchingStrategy.HEMSR)]
    [InlineData(MatchingStrategy.HEMSRdeg)]
    public void AllMatchingStrategies_ProduceValidBisection(MatchingStrategy strategy)
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42, Matching = strategy };
        var result = EdgeSeparator.Bisect(g, opts);
        TestGraphs.AssertValidBisection(g, result, opts);
    }

    // ── Determinism ────────────────────────────────────────────

    [Fact]
    public void Bisect_IsDeterministic_WithSameSeed()
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };

        var r1 = EdgeSeparator.Bisect(g, opts);
        var r2 = EdgeSeparator.Bisect(g, opts);

        Assert.Equal(r1.EdgeCut, r2.EdgeCut);
        Assert.Equal(r1.SizeA, r2.SizeA);
        for (int i = 0; i < g.NodeCount; i++)
            Assert.Equal(r1.Side[i], r2.Side[i]);
    }

    // ── K-way partitioning ─────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void RecursiveBisect_ProducesValidKWay(int k)
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var partId = EdgeSeparator.RecursiveBisect(g, k);
        TestGraphs.AssertValidKWay(g, partId, k);
    }

    [Fact]
    public void RecursiveBisect_K1_AllSamePartition()
    {
        var g = GraphGenerator.BarabasiAlbert(50, 3, seed: 42);
        var partId = EdgeSeparator.RecursiveBisect(g, 1);
        Assert.All(partId, id => Assert.Equal(0, id));
    }

    [Fact]
    public void RecursiveBisect_Barbell_K2_SplitsAtBridge()
    {
        var g = TestGraphs.Barbell(10);
        var partId = EdgeSeparator.RecursiveBisect(g, 2);
        TestGraphs.AssertValidKWay(g, partId, 2);

        // Compute edge cut
        double cut = 0;
        for (int e = 0; e < g.EdgeCount; e++)
            if (partId[g.EdgeSource[e]] != partId[g.EdgeTarget[e]])
                cut += g.EdgeWeight[e];
        Assert.Equal(1.0, cut);
    }

    // ── Balance tolerance ──────────────────────────────────────

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.4)]
    public void DifferentBalanceTolerances(double tolerance)
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42, BalanceTolerance = tolerance };
        var result = EdgeSeparator.Bisect(g, opts);
        TestGraphs.AssertValidBisection(g, result, opts);
    }

    // ── Larger graphs ──────────────────────────────────────────

    [Fact]
    public void LargerGraph_1000Nodes()
    {
        var g = GraphGenerator.BarabasiAlbert(1000, 4, seed: 42);
        var result = EdgeSeparator.Bisect(g);
        TestGraphs.AssertValidBisection(g, result);
    }

    [Fact]
    public void LargerGraph_2000Nodes_KWay8()
    {
        var g = GraphGenerator.BarabasiAlbert(2000, 3, seed: 42);
        var partId = EdgeSeparator.RecursiveBisect(g, 8);
        TestGraphs.AssertValidKWay(g, partId, 8);
    }
}
