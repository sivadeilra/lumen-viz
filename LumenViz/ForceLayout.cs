namespace LumenViz;

/// <summary>
/// Fruchterman–Reingold force-directed graph layout.
///
/// Repulsive forces push all node pairs apart (inverse-square).
/// Attractive forces pull connected nodes together (spring).
/// A cooling schedule gradually reduces displacement, converging
/// to a stable layout.
/// </summary>
public class ForceLayout
{
    private readonly GraphModel _graph;
    private readonly Random _rng = new(42);

    // Layout parameters
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 1000;
    public int Iterations { get; set; } = 300;

    /// <summary>Gravity constant — pulls nodes toward center to prevent drift.</summary>
    public double Gravity { get; set; } = 0.05;

    public ForceLayout(GraphModel graph)
    {
        _graph = graph;
    }

    /// <summary>
    /// Randomize node positions within the layout area.
    /// </summary>
    public void Randomize()
    {
        foreach (var node in _graph.Nodes)
        {
            node.X = _rng.NextDouble() * Width;
            node.Y = _rng.NextDouble() * Height;
        }
    }

    /// <summary>
    /// Run the full layout algorithm.
    /// </summary>
    public void Run()
    {
        int n = _graph.Nodes.Count;
        if (n == 0) return;

        double area = Width * Height;
        double k = Math.Sqrt(area / n);   // optimal edge length
        double k2 = k * k;

        // Initialize random positions if not already set
        bool allZero = _graph.Nodes.All(nd => nd.X == 0 && nd.Y == 0);
        if (allZero) Randomize();

        double[] dx = new double[n];
        double[] dy = new double[n];

        double temp = Width / 10.0;  // initial temperature
        double cooling = temp / (Iterations + 1);

        for (int iter = 0; iter < Iterations; iter++)
        {
            Array.Clear(dx);
            Array.Clear(dy);

            // ── Repulsive forces (all pairs) ───────────────────────────
            // For large graphs (>500 nodes), use a grid-based approximation
            if (n <= 500)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double deltaX = _graph.Nodes[i].X - _graph.Nodes[j].X;
                        double deltaY = _graph.Nodes[i].Y - _graph.Nodes[j].Y;
                        double dist2 = deltaX * deltaX + deltaY * deltaY;
                        if (dist2 < 0.01) dist2 = 0.01;
                        double dist = Math.Sqrt(dist2);

                        double force = k2 / dist;
                        double fx = (deltaX / dist) * force;
                        double fy = (deltaY / dist) * force;

                        dx[i] += fx;
                        dy[i] += fy;
                        dx[j] -= fx;
                        dy[j] -= fy;
                    }
                }
            }
            else
            {
                // Barnes-Hut–style approximation: repel from center of mass
                // For simplicity, use grid-based bucketing
                RepulsiveApprox(k2, dx, dy);
            }

            // ── Attractive forces (edges) ──────────────────────────────
            foreach (var edge in _graph.Edges)
            {
                var ni = _graph.Nodes[edge.Source];
                var nj = _graph.Nodes[edge.Target];
                double deltaX = ni.X - nj.X;
                double deltaY = ni.Y - nj.Y;
                double dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                if (dist < 0.01) dist = 0.01;

                double force = (dist * dist) / k;
                double fx = (deltaX / dist) * force;
                double fy = (deltaY / dist) * force;

                dx[edge.Source] -= fx;
                dy[edge.Source] -= fy;
                dx[edge.Target] += fx;
                dy[edge.Target] += fy;
            }

            // ── Gravity toward center ──────────────────────────────────
            double cx = Width / 2.0;
            double cy = Height / 2.0;
            for (int i = 0; i < n; i++)
            {
                double deltaX = _graph.Nodes[i].X - cx;
                double deltaY = _graph.Nodes[i].Y - cy;
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
                    _graph.Nodes[i].X += dx[i] * scale;
                    _graph.Nodes[i].Y += dy[i] * scale;
                }

                // Keep within bounds (soft constraint)
                _graph.Nodes[i].X = Math.Clamp(_graph.Nodes[i].X, 10, Width - 10);
                _graph.Nodes[i].Y = Math.Clamp(_graph.Nodes[i].Y, 10, Height - 10);
            }

            temp -= cooling;
            if (temp < 0) temp = 0;
        }
    }

    /// <summary>
    /// Grid-based repulsive force approximation for large graphs.
    /// Nodes farther than 2k apart contribute negligible repulsion.
    /// </summary>
    private void RepulsiveApprox(double k2, double[] dx, double[] dy)
    {
        int n = _graph.Nodes.Count;
        double k = Math.Sqrt(k2);
        double cutoff = k * 3;
        double cellSize = cutoff;

        // Build spatial grid
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            int gx = (int)(_graph.Nodes[i].X / cellSize);
            int gy = (int)(_graph.Nodes[i].Y / cellSize);
            var key = (gx, gy);
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                grid[key] = list;
            }
            list.Add(i);
        }

        // For each node, only check neighboring cells
        foreach (var (cell, members) in grid)
        {
            for (int ci = cell.Item1 - 1; ci <= cell.Item1 + 1; ci++)
            {
                for (int cj = cell.Item2 - 1; cj <= cell.Item2 + 1; cj++)
                {
                    if (!grid.TryGetValue((ci, cj), out var neighbors)) continue;

                    foreach (int i in members)
                    {
                        foreach (int j in neighbors)
                        {
                            if (j <= i) continue;

                            double deltaX = _graph.Nodes[i].X - _graph.Nodes[j].X;
                            double deltaY = _graph.Nodes[i].Y - _graph.Nodes[j].Y;
                            double dist2 = deltaX * deltaX + deltaY * deltaY;
                            if (dist2 < 0.01) dist2 = 0.01;
                            double dist = Math.Sqrt(dist2);

                            if (dist > cutoff) continue;

                            double force = k2 / dist;
                            double fx = (deltaX / dist) * force;
                            double fy = (deltaY / dist) * force;

                            dx[i] += fx;
                            dy[i] += fy;
                            dx[j] -= fx;
                            dy[j] -= fy;
                        }
                    }
                }
            }
        }
    }
}
