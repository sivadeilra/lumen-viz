using LumenGraph.Partition;

namespace LumenGraph.Tests;

public class MatchingTests
{
    [Fact]
    public void Triangle_MatchingCoversAtLeastTwoVertices()
    {
        var g = TestGraphs.Triangle();
        var opts = new PartitionOptions { Seed = 42 };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);

        Assert.Equal(3, match.Length);
        // In a triangle, one maximal matching pairs exactly 2 vertices
        int matched = match.Count(m => m >= 0);
        Assert.True(matched >= 2, $"Expected ≥2 matched vertices, got {matched}");
    }

    [Theory]
    [InlineData(MatchingStrategy.Random)]
    [InlineData(MatchingStrategy.HEM)]
    [InlineData(MatchingStrategy.HEMSR)]
    [InlineData(MatchingStrategy.HEMSRdeg)]
    public void AllStrategies_ProduceValidMatching(MatchingStrategy strategy)
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42, Matching = strategy };
        var match = Matching.ComputeMatching(g, strategy, opts);

        Assert.Equal(g.NodeCount, match.Length);

        // Verify: if match[i] = j >= 0, then match[j] = i (symmetric)
        for (int i = 0; i < g.NodeCount; i++)
        {
            if (match[i] >= 0)
            {
                int j = match[i];
                Assert.InRange(j, 0, g.NodeCount - 1);
                Assert.Equal(i, match[j]);
            }
        }

        // No self-matches
        for (int i = 0; i < g.NodeCount; i++)
            Assert.NotEqual(i, match[i]);
    }

    [Theory]
    [InlineData(MatchingStrategy.Random)]
    [InlineData(MatchingStrategy.HEM)]
    public void MatchedPairs_AreEdges_NonSR(MatchingStrategy strategy)
    {
        var g = GraphGenerator.BarabasiAlbert(200, 3, seed: 42);
        var opts = new PartitionOptions { Seed = 42, Matching = strategy };
        var match = Matching.ComputeMatching(g, strategy, opts);

        // Every matched pair (i, match[i]) must be an actual edge
        for (int i = 0; i < g.NodeCount; i++)
        {
            if (match[i] < 0 || match[i] <= i) continue;
            int j = match[i];

            bool found = false;
            var neighbors = g.Neighbors(i);
            for (int ai = 0; ai < neighbors.Length; ai++)
            {
                if (neighbors[ai] == j) { found = true; break; }
            }
            Assert.True(found, $"Matched pair ({i},{j}) is not an edge");
        }
    }

    [Fact]
    public void Path_MatchingIsMaximal()
    {
        // Path of 6: 0-1-2-3-4-5
        // A maximal matching should match at least 4 vertices (2 pairs)
        var g = TestGraphs.Path(6);
        var opts = new PartitionOptions { Seed = 42, Matching = MatchingStrategy.HEM };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);

        int paired = 0;
        for (int i = 0; i < 6; i++)
            if (match[i] >= 0) paired++;

        // Maximal matching on a 6-path should pair at least 4 (i.e., 2+ edges)
        Assert.True(paired >= 4, $"Only {paired} paired on a 6-path");
    }

    [Fact]
    public void SingleNode_EmptyMatching()
    {
        var g = new GraphModel { Title = "Single" };
        g.AllocNodes(1);
        g.AllocEdges(0);
        g.BuildAdjacency();

        var opts = new PartitionOptions();
        var match = Matching.ComputeMatching(g, opts.Matching, opts);
        Assert.Single(match);
        Assert.Equal(-1, match[0]);
    }

    [Fact]
    public void CompleteGraph_MatchingIsNearPerfect()
    {
        var g = GraphGenerator.Complete(20);
        var opts = new PartitionOptions { Seed = 42, Matching = MatchingStrategy.HEM };
        var match = Matching.ComputeMatching(g, opts.Matching, opts);

        int paired = match.Count(m => m >= 0);
        // K_20 has a perfect matching (all 20 paired)
        Assert.Equal(20, paired);
    }
}
