using LumenGraph.Partition;

namespace LumenGraph.Tests;

public class InitialPartitionTests
{
    [Fact]
    public void InitialPartition_Triangle()
    {
        var g = TestGraphs.Triangle();
        var weights = new double[] { 1, 1, 1 };
        var opts = new PartitionOptions { Seed = 42 };

        var result = InitialPartition.Compute(g, weights, opts);

        Assert.Equal(3, result.Side.Length);
        Assert.Equal(3, result.SizeA + result.SizeB);
        Assert.True(result.SizeA > 0 && result.SizeB > 0);
        Assert.True(result.EdgeCut > 0);
    }

    [Fact]
    public void InitialPartition_Barbell_FindsGoodCut()
    {
        // A barbell of K_10 + K_10 connected by a bridge: optimal cut = 1
        var g = TestGraphs.Barbell(10);
        var weights = new double[20];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42 };

        var result = InitialPartition.Compute(g, weights, opts);

        Assert.Equal(20, result.SizeA + result.SizeB);
        Assert.True(result.SizeA > 0 && result.SizeB > 0);
        // On such a small graph, it should find the bridge cut or close
        Assert.True(result.EdgeCut <= 5,
            $"Expected cut ≤5 on barbell(10), got {result.EdgeCut}");
    }

    [Fact]
    public void InitialPartition_RespectsWeightBalance()
    {
        var g = GraphGenerator.BarabasiAlbert(100, 3, seed: 42);
        var weights = new double[100];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42, BalanceTolerance = 0.25 };

        var result = InitialPartition.Compute(g, weights, opts);

        double totalW = 100;
        double targetB = 0.5 * totalW;
        double lo = targetB * 0.75;
        double hi = targetB * 1.25;

        double weightB = 0;
        for (int i = 0; i < 100; i++)
            if (result.Side[i]) weightB += weights[i];

        Assert.InRange(weightB, lo, hi);
    }

    [Fact]
    public void InitialPartition_TwoNodes()
    {
        var g = new GraphModel { Title = "Edge" };
        g.AllocNodes(2);
        g.AllocEdges(1);
        g.EdgeSource[0] = 0;
        g.EdgeTarget[0] = 1;
        g.EdgeWeight[0] = 1;
        g.BuildAdjacency();

        var weights = new double[] { 1, 1 };
        var result = InitialPartition.Compute(g, weights, new PartitionOptions());

        Assert.Equal(1, result.SizeA);
        Assert.Equal(1, result.SizeB);
        Assert.Equal(1.0, result.EdgeCut);
    }
}
