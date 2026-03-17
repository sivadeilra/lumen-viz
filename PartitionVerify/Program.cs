using System.Diagnostics;
using LumenGraph;
using LumenGraph.Partition;

// Verification harness for the Mongoose partition implementation.
// Loads real-world SNAP graphs, runs bisection + k-way partitioning,
// and checks structural invariants.

string dataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data");

var graphs = new (string dir, string file, string label)[]
{
    ("karate", "karate.mtx", "Karate (34)"),
    ("oregon", "oregon.txt", "Oregon AS (10.7K)"),
    ("ca-hepth", "ca-hepth.txt", "ca-HepTh (9.9K)"),
    ("email-enron", "email-enron.txt", "email-Enron (36.7K)"),
    ("loc-brightkite", "loc-brightkite.txt", "loc-Brightkite (58K)"),
};

int totalIssues = 0;

foreach (var (dir, file, label) in graphs)
{
    Console.WriteLine($"\n{new string('═', 71)}");
    Console.WriteLine($"  {label}");
    Console.WriteLine($"{new string('═', 71)}");

    string path = Path.Combine(dataDir, dir, file);
    if (!File.Exists(path))
    {
        Console.WriteLine($"  SKIP — file not found: {path}");
        continue;
    }

    GraphModel graph;
    var sw = Stopwatch.StartNew();
    if (file.EndsWith(".mtx"))
        graph = MatrixMarketReader.ReadFile(path);
    else
        graph = EdgeListReader.ReadFile(path);
    sw.Stop();
    Console.WriteLine($"  Loaded: {graph.NodeCount:N0} nodes, {graph.EdgeCount:N0} edges ({sw.ElapsedMilliseconds} ms)");

    // ── Bisection ──────────────────────────────────────────────
    Console.WriteLine("\n  ── Bisection ──");
    var options = new PartitionOptions { Seed = 42 };
    sw.Restart();
    PartitionResult result;
    try
    {
        result = EdgeSeparator.Bisect(graph, options);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — exception: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"         {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        totalIssues++;
        continue;
    }
    sw.Stop();
    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds} ms");

    // Check invariants
    int issues = VerifyBisection(graph, result, options, label);
    totalIssues += issues;

    // ── 4-way partitioning ─────────────────────────────────────
    Console.WriteLine("\n  ── 4-way Partition ──");
    sw.Restart();
    int[] kway;
    try
    {
        kway = EdgeSeparator.RecursiveBisect(graph, 4, options);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — exception: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"         {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        totalIssues++;
        continue;
    }
    sw.Stop();
    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds} ms");

    issues = VerifyKWay(graph, kway, 4, label);
    totalIssues += issues;

    // ── Try all matching strategies ────────────────────────────
    Console.WriteLine("\n  ── Matching strategy sweep ──");
    foreach (var strategy in Enum.GetValues<MatchingStrategy>())
    {
        var opts = new PartitionOptions { Seed = 42, Matching = strategy };
        sw.Restart();
        try
        {
            var r = EdgeSeparator.Bisect(graph, opts);
            sw.Stop();
            Console.WriteLine($"  {strategy,-12} cut={r.EdgeCut:N0}  A={r.SizeA:N0}  B={r.SizeB:N0}  imbal={r.Imbalance:F3}  ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  {strategy,-12} FAIL — {ex.GetType().Name}: {ex.Message}");
            totalIssues++;
        }
    }
}

Console.WriteLine($"\n{new string('═', 71)}");
if (totalIssues == 0)
    Console.WriteLine("  ALL CHECKS PASSED");
else
    Console.WriteLine($"  {totalIssues} ISSUE(S) FOUND");
Console.WriteLine(new string('═', 71));

return totalIssues > 0 ? 1 : 0;

// ── Verification helpers ──────────────────────────────────────

static int VerifyBisection(GraphModel graph, PartitionResult result, PartitionOptions options, string label)
{
    int issues = 0;
    int n = graph.NodeCount;

    Console.WriteLine($"  EdgeCut: {result.EdgeCut:N0}");
    Console.WriteLine($"  SizeA: {result.SizeA:N0}  SizeB: {result.SizeB:N0}  Imbalance: {result.Imbalance:F3}");

    // 1. All vertices assigned
    if (result.Side.Length != n)
    {
        Console.WriteLine($"  ✗ Side array length {result.Side.Length} ≠ node count {n}");
        issues++;
    }

    // 2. Partition covers all vertices (SizeA + SizeB = n)
    if (result.SizeA + result.SizeB != n)
    {
        Console.WriteLine($"  ✗ SizeA + SizeB = {result.SizeA + result.SizeB} ≠ {n}");
        issues++;
    }

    // 3. Both sides non-empty (for n > 1)
    if (n > 1 && (result.SizeA == 0 || result.SizeB == 0))
    {
        Console.WriteLine($"  ✗ Degenerate partition: A={result.SizeA}, B={result.SizeB}");
        issues++;
    }

    // 4. EdgeCut is non-negative
    if (result.EdgeCut < 0)
    {
        Console.WriteLine($"  ✗ Negative edge cut: {result.EdgeCut}");
        issues++;
    }

    // 5. EdgeCut > 0 for connected graphs with n > 1
    if (n > 1 && result.EdgeCut == 0)
    {
        Console.WriteLine($"  ⚠ Edge cut is zero — graph may be disconnected or partition is trivial");
    }

    // 6. Verify edge cut computation independently
    double recomputedCut = 0;
    for (int e = 0; e < graph.EdgeCount; e++)
    {
        if (result.Side[graph.EdgeSource[e]] != result.Side[graph.EdgeTarget[e]])
            recomputedCut += graph.EdgeWeight[e];
    }
    if (Math.Abs(recomputedCut - result.EdgeCut) > 1e-6)
    {
        Console.WriteLine($"  ✗ Recomputed cut {recomputedCut:N2} ≠ stored {result.EdgeCut:N2}");
        issues++;
    }
    else
    {
        Console.WriteLine($"  ✓ Edge cut verified independently");
    }

    // 7. Imbalance within tolerance
    double maxImbalance = 1.0 + options.BalanceTolerance;
    if (result.Imbalance > maxImbalance)
    {
        Console.WriteLine($"  ✗ Imbalance {result.Imbalance:F3} exceeds tolerance {maxImbalance:F3}");
        issues++;
    }
    else
    {
        Console.WriteLine($"  ✓ Balance within tolerance (≤{maxImbalance:F3})");
    }

    // 8. Recount from Side[] directly
    int countA = 0, countB = 0;
    for (int i = 0; i < n; i++)
    {
        if (result.Side[i]) countB++;
        else countA++;
    }
    if (countA != result.SizeA || countB != result.SizeB)
    {
        Console.WriteLine($"  ✗ Size mismatch: counted A={countA}/B={countB} vs stored A={result.SizeA}/B={result.SizeB}");
        issues++;
    }
    else
    {
        Console.WriteLine($"  ✓ Size counts consistent");
    }

    if (issues == 0)
        Console.WriteLine($"  ✓ All bisection checks passed");
    return issues;
}

static int VerifyKWay(GraphModel graph, int[] partId, int k, string label)
{
    int issues = 0;
    int n = graph.NodeCount;

    if (partId.Length != n)
    {
        Console.WriteLine($"  ✗ partId length {partId.Length} ≠ node count {n}");
        issues++;
    }

    // Count partition sizes
    var sizes = new Dictionary<int, int>();
    for (int i = 0; i < n; i++)
    {
        int pid = partId[i];
        sizes.TryGetValue(pid, out int count);
        sizes[pid] = count + 1;
    }

    int actualK = sizes.Count;
    Console.WriteLine($"  Parts: {actualK} (requested {k})");

    if (actualK > k)
    {
        Console.WriteLine($"  ✗ More parts than requested: {actualK} > {k}");
        issues++;
    }

    // Print sizes
    foreach (var (pid, count) in sizes.OrderBy(kv => kv.Key))
        Console.Write($"    [{pid}]={count:N0}  ");
    Console.WriteLine();

    // Verify all vertices have valid partition IDs
    for (int i = 0; i < n; i++)
    {
        if (partId[i] < 0 || partId[i] >= k)
        {
            Console.WriteLine($"  ✗ Vertex {i} has invalid partition ID {partId[i]}");
            issues++;
            break;
        }
    }

    // Compute k-way edge cut
    double kwCut = 0;
    for (int e = 0; e < graph.EdgeCount; e++)
    {
        if (partId[graph.EdgeSource[e]] != partId[graph.EdgeTarget[e]])
            kwCut += graph.EdgeWeight[e];
    }
    Console.WriteLine($"  K-way edge cut: {kwCut:N0}");

    if (issues == 0)
        Console.WriteLine($"  ✓ All k-way checks passed");
    return issues;
}
