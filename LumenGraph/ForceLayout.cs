namespace LumenGraph;

/// <summary>
/// Fruchterman–Reingold force-directed graph layout.
///
/// Repulsive forces push all node pairs apart (inverse-square).
/// Attractive forces pull connected nodes together (spring).
/// A cooling schedule gradually reduces displacement, converging
/// to a stable layout.
///
/// Operates directly on GraphModel's parallel arrays (NodeX, NodeY)
/// for cache-friendly, allocation-free iteration.
/// </summary>
public class ForceLayout
{
    private readonly GraphModel _graph;
    private readonly Random _rng = new(42);
    private QuadTree? _quadTree;

    // Layout parameters
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 1000;
    public int Iterations { get; set; } = 300;

    /// <summary>Gravity constant — pulls nodes toward center to prevent drift.</summary>
    public double Gravity { get; set; } = 0.05;

    /// <summary>
    /// When true, nodes are confined inside Width×Height with soft
    /// boundary pull-back. When false (default), layout runs in unbounded
    /// free space — k is derived purely from node count and no boundary
    /// forces are applied.
    /// </summary>
    public bool Bounded { get; set; } = false;

    public ForceLayout(GraphModel graph)
    {
        _graph = graph;
    }

    /// <summary>
    /// Randomize node positions within the layout area.
    /// </summary>
    public void Randomize()
    {
        var px = _graph.NodeX;
        var py = _graph.NodeY;
        if (Bounded)
        {
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                px[i] = _rng.NextDouble() * Width;
                py[i] = _rng.NextDouble() * Height;
            }
        }
        else
        {
            // Unbounded: scatter in a circle of radius ~ sqrt(n) * 10
            double radius = Math.Sqrt(_graph.NodeCount) * 10;
            for (int i = 0; i < _graph.NodeCount; i++)
            {
                double angle = _rng.NextDouble() * 2 * Math.PI;
                double r = _rng.NextDouble() * radius;
                px[i] = r * Math.Cos(angle);
                py[i] = r * Math.Sin(angle);
            }
        }
    }

    /// <summary>
    /// Run the full layout algorithm.
    /// </summary>
    public void Run()
    {
        int n = _graph.NodeCount;
        if (n == 0) return;

        // Optimal edge length k:
        // Bounded: derived from box area.  Unbounded: from node count.
        double k;
        if (Bounded)
            k = Math.Sqrt(Width * Height / n);
        else
            k = Math.Sqrt(10000.0 / Math.Sqrt(n));  // ~constant density
        double k2 = k * k;

        // Alias the position arrays for tight inner loops
        var px = _graph.NodeX;
        var py = _graph.NodeY;

        // Initialize random positions if not already set
        bool allZero = true;
        for (int i = 0; i < n; i++)
        {
            if (px[i] != 0 || py[i] != 0) { allZero = false; break; }
        }
        if (allZero) Randomize();

        double[] dx = new double[n];
        double[] dy = new double[n];

        double temp = Bounded ? Width / 10.0 : k * 5.0;  // initial temperature
        double cooling = temp / (Iterations + 1);

        for (int iter = 0; iter < Iterations; iter++)
        {
            Array.Clear(dx);
            Array.Clear(dy);

            // ── Repulsive forces (all pairs) ───────────────────────────
            if (n <= 500)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double deltaX = px[i] - px[j];
                        double deltaY = py[i] - py[j];
                        double dist2 = deltaX * deltaX + deltaY * deltaY;
                        if (dist2 < 0.01) dist2 = 0.01;
                        double dist = Math.Sqrt(dist2);

                        double force = k2 / dist;
                        double fx = (deltaX / dist) * force;
                        double fy = (deltaY / dist) * force;

                        dx[i] += fx; dy[i] += fy;
                        dx[j] -= fx; dy[j] -= fy;
                    }
                }
            }
            else
            {
                // Barnes-Hut quadtree: O(n log n) repulsive approximation
                _quadTree ??= new QuadTree();
                _quadTree.Build(px, py, n);
                _quadTree.ComputeRepulsion(px, py, n, k2, dx, dy);
            }

            // ── Attractive forces (edges) ──────────────────────────────
            var es = _graph.EdgeSource;
            var et = _graph.EdgeTarget;
            for (int e = 0; e < _graph.EdgeCount; e++)
            {
                int si = es[e], ti = et[e];
                double deltaX = px[si] - px[ti];
                double deltaY = py[si] - py[ti];
                double dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                if (dist < 0.01) dist = 0.01;

                double force = (dist * dist) / k;
                double fx = (deltaX / dist) * force;
                double fy = (deltaY / dist) * force;

                dx[si] -= fx; dy[si] -= fy;
                dx[ti] += fx; dy[ti] += fy;
            }

            // ── Gravity toward center ──────────────────────────────────
            // Bounded: pull toward box center.  Unbounded: pull toward origin.
            double cx = Bounded ? Width / 2.0 : 0.0;
            double cy = Bounded ? Height / 2.0 : 0.0;
            for (int i = 0; i < n; i++)
            {
                double deltaX = px[i] - cx;
                double deltaY = py[i] - cy;
                double dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                if (dist > 0.01)
                {
                    dx[i] -= Gravity * deltaX;
                    dy[i] -= Gravity * deltaY;
                }
            }

            // ── Apply displacements, clamped by temperature ────────────
            for (int i = 0; i < n; i++)
            {
                double disp = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (disp > 0.01)
                {
                    double scale = Math.Min(disp, temp) / disp;
                    px[i] += dx[i] * scale;
                    py[i] += dy[i] * scale;
                }

                // Bounded mode: soft pull toward box interior.
                // Unbounded mode: no boundary at all — gravity alone prevents drift.
                if (Bounded)
                {
                    double margin = 50;
                    double pullStrength = 0.1;
                    if (px[i] < margin)          px[i] += (margin - px[i]) * pullStrength;
                    if (px[i] > Width - margin)  px[i] -= (px[i] - (Width - margin)) * pullStrength;
                    if (py[i] < margin)          py[i] += (margin - py[i]) * pullStrength;
                    if (py[i] > Height - margin) py[i] -= (py[i] - (Height - margin)) * pullStrength;
                }
            }

            temp -= cooling;
            if (temp < 0) temp = 0;
        }
    }

}
