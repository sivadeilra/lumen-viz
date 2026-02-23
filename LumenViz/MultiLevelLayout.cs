namespace LumenViz;

/// <summary>
/// Multi-level force-directed layout using HEM coarsening.
///
/// Three phases:
/// 1. Coarsen: Build hierarchy via Heavy-Edge Matching (~log₂(n) levels)
/// 2. Layout:  FR on coarsest graph (cheap, captures global structure)
/// 3. Uncoarsen: Expand each level, placing children at parent + jitter,
///    then refine with a few FR iterations
///
/// Combines naturally with Barnes-Hut quadtree (used inside ForceLayout)
/// for O(n log n) per iteration at every level.
///
/// This is the same approach used by sfdp (Graphviz), the gold standard
/// for large force-directed graph layout.
/// </summary>
public sealed class MultiLevelLayout
{
    private readonly GraphModel _graph;

    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 1000;
    public double Gravity { get; set; } = 0.05;

    /// <summary>FR iterations for the coarsest level (default 500).</summary>
    public int CoarseIterations { get; set; } = 500;

    /// <summary>FR iterations per uncoarsening refinement level (default 50).</summary>
    public int RefineIterations { get; set; } = 50;

    /// <summary>Stop coarsening when node count falls below this (default 50).</summary>
    public int MinCoarseSize { get; set; } = 50;

    public MultiLevelLayout(GraphModel graph)
    {
        _graph = graph;
    }

    public void Run()
    {
        int n = _graph.NodeCount;

        // Small graphs: just run flat FR directly
        if (n <= MinCoarseSize * 2)
        {
            RunFlat(_graph, CoarseIterations, randomize: true);
            return;
        }

        // Phase 1: Build coarsening hierarchy
        var levels = Coarsening.BuildHierarchy(_graph, MinCoarseSize);

        if (levels.Count == 0)
        {
            // Couldn't coarsen meaningfully — fall back to flat FR
            RunFlat(_graph, CoarseIterations, randomize: true);
            return;
        }

        // Phase 2: Layout the coarsest graph
        var coarsest = levels[^1].Graph;
        RunFlat(coarsest, CoarseIterations, randomize: true);

        // Phase 3: Uncoarsen with refinement (coarsest → finest)
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            var level = levels[i];
            // The "fine" graph at this step: original if i==0, else the previous level's graph
            var fineGraph = (i == 0) ? _graph : levels[i - 1].Graph;
            var coarseGraph = level.Graph;

            // Place fine nodes at their coarse parent's position + jitter
            PlaceFromCoarse(fineGraph, coarseGraph, level.FineToCoarse);

            // Refine — don't randomize, the placement from coarser level is already good
            RunFlat(fineGraph, RefineIterations, randomize: false);
        }
    }

    /// <summary>
    /// Run flat Fruchterman-Reingold layout on a graph.
    /// </summary>
    private void RunFlat(GraphModel graph, int iterations, bool randomize)
    {
        var layout = new ForceLayout(graph)
        {
            Width = Width,
            Height = Height,
            Gravity = Gravity,
            Iterations = iterations,
        };

        if (randomize)
            layout.Randomize();

        layout.Run();
    }

    /// <summary>
    /// Position each fine node at its coarse parent's coordinates plus
    /// a small random jitter to break symmetry. Without jitter, merged
    /// nodes would start exactly overlapping and FR's repulsive force
    /// would be undefined (0/0).
    /// </summary>
    private static void PlaceFromCoarse(
        GraphModel fine, GraphModel coarse, int[] fineToCoarse)
    {
        var rng = new Random(42);
        double jitter = 5.0; // small offset to break symmetry

        for (int i = 0; i < fine.NodeCount; i++)
        {
            int ci = fineToCoarse[i];
            fine.NodeX[i] = coarse.NodeX[ci] + (rng.NextDouble() - 0.5) * jitter;
            fine.NodeY[i] = coarse.NodeY[ci] + (rng.NextDouble() - 0.5) * jitter;
        }
    }
}
