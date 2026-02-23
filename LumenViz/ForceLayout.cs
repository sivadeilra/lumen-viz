namespace LumenViz;

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
        var px = _graph.NodeX;
        var py = _graph.NodeY;
        for (int i = 0; i < _graph.NodeCount; i++)
        {
            px[i] = _rng.NextDouble() * Width;
            py[i] = _rng.NextDouble() * Height;
        }
    }

    /// <summary>
    /// Run the full layout algorithm.
    /// </summary>
    public void Run()
    {
        int n = _graph.NodeCount;
        if (n == 0) return;

        double area = Width * Height;
        double k = Math.Sqrt(area / n);   // optimal edge length
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

        double temp = Width / 10.0;  // initial temperature
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
                RepulsiveApprox(k2, dx, dy);
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
            double cx = Width / 2.0;
            double cy = Height / 2.0;
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

                // Keep within bounds (soft constraint)
                px[i] = Math.Clamp(px[i], 10, Width - 10);
                py[i] = Math.Clamp(py[i], 10, Height - 10);
            }

            temp -= cooling;
            if (temp < 0) temp = 0;
        }
    }

    /// <summary>
    /// Grid-based repulsive force approximation for large graphs.
    /// Reuses a single flat array for grid cells instead of
    /// Dictionary{(int,int), List{int}} — eliminates ~N*iter allocations.
    /// </summary>
    private void RepulsiveApprox(double k2, double[] dx, double[] dy)
    {
        int n = _graph.NodeCount;
        double k = Math.Sqrt(k2);
        double cutoff = k * 3;
        double cellSize = cutoff;

        var px = _graph.NodeX;
        var py = _graph.NodeY;

        // Compute grid bounds
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (px[i] < minX) minX = px[i];
            if (py[i] < minY) minY = py[i];
            if (px[i] > maxX) maxX = px[i];
            if (py[i] > maxY) maxY = py[i];
        }

        int gridW = Math.Max(1, (int)((maxX - minX) / cellSize) + 2);
        int gridH = Math.Max(1, (int)((maxY - minY) / cellSize) + 2);
        int totalCells = gridW * gridH;

        // Count nodes per cell
        var cellCount = new int[totalCells];
        var nodeCell = new int[n]; // which cell each node belongs to

        for (int i = 0; i < n; i++)
        {
            int gx = Math.Clamp((int)((px[i] - minX) / cellSize), 0, gridW - 1);
            int gy = Math.Clamp((int)((py[i] - minY) / cellSize), 0, gridH - 1);
            int cell = gy * gridW + gx;
            nodeCell[i] = cell;
            cellCount[cell]++;
        }

        // Build cell offsets (prefix sum) and cell member list
        var cellOffset = new int[totalCells + 1];
        for (int c = 0; c < totalCells; c++)
            cellOffset[c + 1] = cellOffset[c] + cellCount[c];

        var cellMembers = new int[n];
        var cursor = new int[totalCells];
        Array.Copy(cellOffset, cursor, totalCells);
        for (int i = 0; i < n; i++)
            cellMembers[cursor[nodeCell[i]]++] = i;

        // For each cell, check neighboring cells
        for (int cy2 = 0; cy2 < gridH; cy2++)
        {
            for (int cx2 = 0; cx2 < gridW; cx2++)
            {
                int cellA = cy2 * gridW + cx2;
                int startA = cellOffset[cellA];
                int endA = cellOffset[cellA + 1];
                if (startA == endA) continue;

                for (int ny = cy2 - 1; ny <= cy2 + 1; ny++)
                {
                    if (ny < 0 || ny >= gridH) continue;
                    for (int nx = cx2 - 1; nx <= cx2 + 1; nx++)
                    {
                        if (nx < 0 || nx >= gridW) continue;
                        int cellB = ny * gridW + nx;
                        int startB = cellOffset[cellB];
                        int endB = cellOffset[cellB + 1];
                        if (startB == endB) continue;

                        // Same cell: only do i < j pairs
                        bool sameCell = (cellA == cellB);

                        for (int ai = startA; ai < endA; ai++)
                        {
                            int i = cellMembers[ai];
                            int jStart = sameCell ? ai + 1 : startB;
                            for (int bi = jStart; bi < endB; bi++)
                            {
                                int j = cellMembers[bi];

                                double deltaX = px[i] - px[j];
                                double deltaY = py[i] - py[j];
                                double dist2 = deltaX * deltaX + deltaY * deltaY;
                                if (dist2 < 0.01) dist2 = 0.01;
                                double dist = Math.Sqrt(dist2);

                                if (dist > cutoff) continue;

                                double force = k2 / dist;
                                double fx = (deltaX / dist) * force;
                                double fy = (deltaY / dist) * force;

                                dx[i] += fx; dy[i] += fy;
                                dx[j] -= fx; dy[j] -= fy;
                            }
                        }
                    }
                }
            }
        }
    }
}
