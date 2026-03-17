using LumenGraph.Partition;

namespace LumenGraph.Tests;

public class RefinementTests
{
    [Fact]
    public void QpRefine_DoesNotWorsenCut_OnSmallGraph()
    {
        var g = GraphGenerator.BarabasiAlbert(50, 3, seed: 42);
        var weights = new double[50];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42, QpIterations = 10 };

        // Create a random initial partition
        var rng = new Random(42);
        var partition = new PartitionResult(50);
        for (int i = 0; i < 50; i++)
            partition.Side[i] = rng.NextDouble() > 0.5;
        partition.Recompute(g);

        double cutBefore = partition.EdgeCut;

        QpRefine.Refine(g, partition, weights, opts);

        Assert.True(partition.EdgeCut <= cutBefore + 1e-6,
            $"QP worsened cut: {cutBefore} → {partition.EdgeCut}");
    }

    [Fact]
    public void FmRefine_DoesNotWorsenCut_OnSmallGraph()
    {
        var g = GraphGenerator.BarabasiAlbert(50, 3, seed: 42);
        var weights = new double[50];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42, FmPasses = 4 };

        // Create a balanced initial partition
        var partition = new PartitionResult(50);
        for (int i = 0; i < 25; i++) partition.Side[i] = false;
        for (int i = 25; i < 50; i++) partition.Side[i] = true;
        partition.Recompute(g);

        double cutBefore = partition.EdgeCut;

        FmRefine.Refine(g, partition, weights, opts);

        Assert.True(partition.EdgeCut <= cutBefore + 1e-6,
            $"FM worsened cut: {cutBefore} → {partition.EdgeCut}");
    }

    [Fact]
    public void FmRefine_MaintainsBalanceConstraint()
    {
        var g = GraphGenerator.BarabasiAlbert(100, 3, seed: 42);
        var weights = new double[100];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42, BalanceTolerance = 0.25 };

        // Start with a balanced partition
        var partition = new PartitionResult(100);
        for (int i = 0; i < 50; i++) partition.Side[i] = false;
        for (int i = 50; i < 100; i++) partition.Side[i] = true;
        partition.Recompute(g);

        FmRefine.Refine(g, partition, weights, opts);

        double maxImbal = 1.0 + opts.BalanceTolerance;
        Assert.True(partition.Imbalance <= maxImbal + 1e-9,
            $"FM broke balance: imbalance = {partition.Imbalance:F4}");
    }

    [Fact]
    public void FmRefine_CanRepairImbalancedPartition()
    {
        var g = GraphGenerator.BarabasiAlbert(100, 3, seed: 42);
        var weights = new double[100];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42, BalanceTolerance = 0.25 };

        // Start with a severely imbalanced partition: 10 vs 90
        var partition = new PartitionResult(100);
        for (int i = 0; i < 10; i++) partition.Side[i] = true;
        for (int i = 10; i < 100; i++) partition.Side[i] = false;
        partition.Recompute(g);

        Assert.True(partition.Imbalance > 1.5, "Setup: should start imbalanced");

        FmRefine.Refine(g, partition, weights, opts);

        // FM with corrective moves should improve balance
        Assert.True(partition.Imbalance < 1.5,
            $"FM should have improved balance from >1.5, got {partition.Imbalance:F4}");
    }

    [Fact]
    public void QpRefine_PreservesVertexCoverage()
    {
        var g = GraphGenerator.BarabasiAlbert(100, 3, seed: 42);
        var weights = new double[100];
        Array.Fill(weights, 1.0);
        var opts = new PartitionOptions { Seed = 42 };

        var partition = new PartitionResult(100);
        for (int i = 0; i < 50; i++) partition.Side[i] = false;
        for (int i = 50; i < 100; i++) partition.Side[i] = true;
        partition.Recompute(g);

        QpRefine.Refine(g, partition, weights, opts);

        Assert.Equal(100, partition.Side.Length);
        Assert.Equal(100, partition.SizeA + partition.SizeB);
    }
}
