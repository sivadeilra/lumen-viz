namespace LumenGraph;

/// <summary>
/// Barnes-Hut quadtree for O(n log n) repulsive force approximation.
///
/// Recursively partitions 2D space into quadrants. Each internal node
/// stores its subtree's total mass and center of mass. When computing
/// repulsion for a node, distant cells are treated as a single body
/// (controlled by the θ parameter).
///
/// Replaces the grid-based RepulsiveApprox with better accuracy
/// (no distance cutoff) and adaptive resolution.
///
/// Uses a flat array representation — no per-node heap allocations.
/// Each tree node occupies one slot; children are indexed via
/// _children[node * 4 + quadrant].
/// </summary>
public sealed class QuadTree
{
    // Per-node data (flat arrays, indexed by tree node ID)
    private double[] _cx = null!;       // center of mass X
    private double[] _cy = null!;       // center of mass Y
    private double[] _mass = null!;     // total mass (node count) in this subtree
    private double[] _cellX = null!;    // cell top-left X
    private double[] _cellY = null!;    // cell top-left Y
    private double[] _cellSize = null!; // cell width (square cells)
    private int[] _children = null!;    // 4 children per node: [node*4 + 0..3], -1 = empty
    private int[] _bodyIndex = null!;   // -1 if internal, else the original node index

    private int _count;         // number of tree nodes allocated
    private int _capacity;

    /// <summary>
    /// θ (theta) parameter controlling accuracy vs speed.
    /// Lower = more accurate, slower. Typical: 0.8–1.2 for graph drawing.
    /// θ=0 → exact (all leaves visited). θ=1.0 → good default.
    /// </summary>
    public double Theta { get; set; } = 1.0;

    public QuadTree(int initialCapacity = 4096)
    {
        _capacity = initialCapacity;
        AllocArrays();
    }

    private void AllocArrays()
    {
        _cx = new double[_capacity];
        _cy = new double[_capacity];
        _mass = new double[_capacity];
        _cellX = new double[_capacity];
        _cellY = new double[_capacity];
        _cellSize = new double[_capacity];
        _children = new int[_capacity * 4];
        _bodyIndex = new int[_capacity];
    }

    /// <summary>
    /// Build the quadtree from node positions. Can be called repeatedly
    /// (e.g. once per FR iteration) — reuses internal storage.
    /// </summary>
    public void Build(double[] px, double[] py, int n)
    {
        // Reset — reuse existing arrays if large enough
        _count = 0;
        if (n == 0) return;

        // Ensure capacity (a balanced tree needs ~2n nodes; we size generously)
        int needed = Math.Max(4 * n, 4096);
        if (needed > _capacity)
        {
            _capacity = needed;
            AllocArrays();
        }

        // Find bounding box
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (px[i] < minX) minX = px[i];
            if (py[i] < minY) minY = py[i];
            if (px[i] > maxX) maxX = px[i];
            if (py[i] > maxY) maxY = py[i];
        }

        // Make it square with some padding
        double size = Math.Max(maxX - minX, maxY - minY) + 1.0;
        double ox = (minX + maxX) / 2.0 - size / 2.0;
        double oy = (minY + maxY) / 2.0 - size / 2.0;

        // Create root node
        int root = AllocNode();
        _cellX[root] = ox;
        _cellY[root] = oy;
        _cellSize[root] = size;

        // Insert all bodies
        for (int i = 0; i < n; i++)
            Insert(root, i, px[i], py[i]);
    }

    /// <summary>
    /// Compute repulsive forces on all nodes using Barnes-Hut traversal.
    /// Forces are accumulated into dx[], dy[].
    /// </summary>
    public void ComputeRepulsion(double[] px, double[] py, int n,
        double k2, double[] dx, double[] dy)
    {
        if (_count == 0) return;
        for (int i = 0; i < n; i++)
            ComputeForce(0, i, px[i], py[i], k2, dx, dy);
    }

    // ── Tree construction ──────────────────────────────────────────────

    private int AllocNode()
    {
        if (_count >= _capacity)
            Grow();

        int idx = _count++;
        _cx[idx] = 0;
        _cy[idx] = 0;
        _mass[idx] = 0;
        _bodyIndex[idx] = -1;
        int ci = idx * 4;
        _children[ci] = -1;
        _children[ci + 1] = -1;
        _children[ci + 2] = -1;
        _children[ci + 3] = -1;
        return idx;
    }

    private void Grow()
    {
        _capacity *= 2;
        Array.Resize(ref _cx, _capacity);
        Array.Resize(ref _cy, _capacity);
        Array.Resize(ref _mass, _capacity);
        Array.Resize(ref _cellX, _capacity);
        Array.Resize(ref _cellY, _capacity);
        Array.Resize(ref _cellSize, _capacity);
        Array.Resize(ref _children, _capacity * 4);
        Array.Resize(ref _bodyIndex, _capacity);
    }

    private void Insert(int node, int body, double bx, double by)
    {
        // If this node is empty (mass == 0), place the body here as a leaf
        if (_mass[node] == 0)
        {
            _cx[node] = bx;
            _cy[node] = by;
            _mass[node] = 1;
            _bodyIndex[node] = body;
            return;
        }

        // If cell is too small, just accumulate mass (handles coincident points)
        if (_cellSize[node] < 1e-6)
        {
            double total = _mass[node] + 1;
            _cx[node] = (_cx[node] * _mass[node] + bx) / total;
            _cy[node] = (_cy[node] * _mass[node] + by) / total;
            _mass[node] = total;
            _bodyIndex[node] = -1;
            return;
        }

        // If this node is a leaf (has a single body), push it into a child
        if (_bodyIndex[node] >= 0)
        {
            int existing = _bodyIndex[node];
            double ex = _cx[node];
            double ey = _cy[node];
            _bodyIndex[node] = -1; // now an internal node
            InsertIntoChild(node, existing, ex, ey);
        }

        // Update center of mass
        double totalMass = _mass[node] + 1;
        _cx[node] = (_cx[node] * _mass[node] + bx) / totalMass;
        _cy[node] = (_cy[node] * _mass[node] + by) / totalMass;
        _mass[node] = totalMass;

        // Insert the new body into the appropriate child quadrant
        InsertIntoChild(node, body, bx, by);
    }

    private void InsertIntoChild(int node, int body, double bx, double by)
    {
        double halfSize = _cellSize[node] / 2.0;
        double midX = _cellX[node] + halfSize;
        double midY = _cellY[node] + halfSize;

        // Determine quadrant: 0=NW, 1=NE, 2=SW, 3=SE
        int quadrant;
        double childX, childY;
        if (bx <= midX)
        {
            if (by <= midY) { quadrant = 0; childX = _cellX[node]; childY = _cellY[node]; }
            else { quadrant = 2; childX = _cellX[node]; childY = midY; }
        }
        else
        {
            if (by <= midY) { quadrant = 1; childX = midX; childY = _cellY[node]; }
            else { quadrant = 3; childX = midX; childY = midY; }
        }

        int ci = node * 4 + quadrant;
        if (_children[ci] < 0)
        {
            int child = AllocNode();
            _cellX[child] = childX;
            _cellY[child] = childY;
            _cellSize[child] = halfSize;
            _children[ci] = child;
        }

        Insert(_children[ci], body, bx, by);
    }

    // ── Force computation ──────────────────────────────────────────────

    private void ComputeForce(int node, int body, double bx, double by,
        double k2, double[] dx, double[] dy)
    {
        if (node < 0 || _mass[node] == 0) return;

        // Skip self-interaction (single-body leaf containing this body)
        if (_bodyIndex[node] == body) return;

        double deltaX = bx - _cx[node];
        double deltaY = by - _cy[node];
        double dist2 = deltaX * deltaX + deltaY * deltaY;
        double size = _cellSize[node];

        // Barnes-Hut criterion: if leaf OR (s/d < θ), treat as single mass
        if (_bodyIndex[node] >= 0 || (size * size < Theta * Theta * dist2))
        {
            if (dist2 < 0.01) dist2 = 0.01;
            double dist = Math.Sqrt(dist2);
            double force = k2 * _mass[node] / dist;
            dx[body] += (deltaX / dist) * force;
            dy[body] += (deltaY / dist) * force;
            return;
        }

        // Recurse into children
        int ci = node * 4;
        if (_children[ci] >= 0) ComputeForce(_children[ci], body, bx, by, k2, dx, dy);
        if (_children[ci + 1] >= 0) ComputeForce(_children[ci + 1], body, bx, by, k2, dx, dy);
        if (_children[ci + 2] >= 0) ComputeForce(_children[ci + 2], body, bx, by, k2, dx, dy);
        if (_children[ci + 3] >= 0) ComputeForce(_children[ci + 3], body, bx, by, k2, dx, dy);
    }
}
