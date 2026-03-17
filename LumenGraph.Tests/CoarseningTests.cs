using LumenGraph.Partition;

namespace LumenGraph.Tests;

public class CoarseningTests
{
    [Fact]
    public void Coarsen_ReducesNodeCount()
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        var weights = new double[g.NodeCount];
        Array.Fill(weights, 1.0);

        var level = GraphCoarsening.Coarsen(g, match, weights);

        Assert.True(level.Graph.NodeCount < g.NodeCount,
            $"Coarse graph ({level.Graph.NodeCount}) should be smaller than original ({g.NodeCount})");
        Assert.True(level.Graph.NodeCount > 0);
    }

    [Fact]
    public void FineToCoarse_MapsAllVertices()
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        var weights = new double[g.NodeCount];
        Array.Fill(weights, 1.0);

        var level = GraphCoarsening.Coarsen(g, match, weights);

        Assert.Equal(g.NodeCount, level.FineToCoarse.Length);

        // All fine-to-coarse IDs should be valid coarse node IDs
        int coarseN = level.Graph.NodeCount;
        for (int i = 0; i < g.NodeCount; i++)
            Assert.InRange(level.FineToCoarse[i], 0, coarseN - 1);
    }

    [Fact]
    public void WeightConservation()
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        var weights = new double[g.NodeCount];
        Array.Fill(weights, 1.0);

        var level = GraphCoarsening.Coarsen(g, match, weights);

        // Total vertex weight must be preserved
        double fineTotalWeight = weights.Sum();
        double coarseTotalWeight = level.VertexWeight.Sum();
        Assert.Equal(fineTotalWeight, coarseTotalWeight, precision: 6);
    }

    [Fact]
    public void MatchedPairs_MapToSameCoarseNode()
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        var weights = new double[g.NodeCount];
        Array.Fill(weights, 1.0);

        var level = GraphCoarsening.Coarsen(g, match, weights);

        for (int i = 0; i < g.NodeCount; i++)
        {
            if (match[i] >= 0 && match[i] > i)
            {
                int j = match[i];
                Assert.Equal(level.FineToCoarse[i], level.FineToCoarse[j]);
            }
        }
    }

    [Fact]
    public void BuildHierarchy_RespectsCoarsenLimit()
    {
        var g = GraphGenerator.BarabasiAlbert(500, 3, seed: 42);
        var opts = new PartitionOptions { CoarsenLimit = 32, Seed = 42 };

        var levels = GraphCoarsening.BuildHierarchy(g, opts);

        Assert.True(levels.Count > 0, "Should produce at least one coarsening level");

        // The coarsest graph should be near the CoarsenLimit
        var coarsest = levels[^1].Graph;
        Assert.True(coarsest.NodeCount <= opts.CoarsenLimit * 2,
            $"Coarsest graph has {coarsest.NodeCount} nodes, expected near {opts.CoarsenLimit}");
    }

    [Fact]
    public void HierarchyLevels_MonotonicallyDecreaseSize()
    {
        var g = GraphGenerator.BarabasiAlbert(500, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42 };

        var levels = GraphCoarsening.BuildHierarchy(g, opts);

        int prevSize = g.NodeCount;
        foreach (var level in levels)
        {
            Assert.True(level.Graph.NodeCount < prevSize,
                $"Coarsening should reduce: {level.Graph.NodeCount} ≥ {prevSize}");
            prevSize = level.Graph.NodeCount;
        }
    }

    [Fact]
    public void Coarsen_SmallGraph_ProducesValidResult()
    {
        // Triangle: 3 nodes. Matching pairs 2, coarsens to 2 nodes.
        var g = TestGraphs.Triangle();
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        var weights = new double[] { 1, 1, 1 };

        var level = GraphCoarsening.Coarsen(g, match, weights);

        Assert.Equal(2, level.Graph.NodeCount);
        Assert.Equal(3, level.FineToCoarse.Length);
        Assert.Equal(2, level.VertexWeight.Length);
        Assert.Equal(3.0, level.VertexWeight.Sum(), precision: 6);
    }
}
