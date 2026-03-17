using System.ComponentModel;
using System.Diagnostics;
using SkiaSharp;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// Adjacency matrix viewer — displays a graph's sparse structure as a
/// square heatmap. "From" vertices on the Y-axis (top-to-bottom), "To"
/// vertices on the X-axis (left-to-right). Black background.
///
/// Since node count typically exceeds pixel resolution, multiple graph
/// cells map to each screen pixel — a natural density heatmap. Uses
/// SkiaSharp for bitmap-level rendering.
/// </summary>
public class MatrixView : Control, IGraphViewer
{
    public string ViewerType => "matrix";

    // ── Graph data ──────────────────────────────────────────────────────
    private GraphModel? _graph;
    private CoarseningHierarchy? _hierarchy;
    private int _currentLevel;

    // ── Node ordering ───────────────────────────────────────────────────
    /// <summary>
    /// Maps visual position → node index. ordering[visualRow] = nodeIndex.
    /// </summary>
    private int[] _ordering = Array.Empty<int>();

    /// <summary>
    /// Inverse: maps nodeIndex → visual position. Used for highlight lookup.
    /// </summary>
    private int[] _inverseOrder = Array.Empty<int>();

    private string _orderingMode = "community";

    // ── Viewport (zoom/pan in matrix space) ─────────────────────────────
    /// <summary>Top-left corner of the visible region in matrix coords [0..N).</summary>
    private double _viewX, _viewY;
    /// <summary>Size of the visible region in matrix coords.</summary>
    private double _viewW, _viewH;

    // ── Mouse state ─────────────────────────────────────────────────────
    private Point _lastMouse;
    private bool _panning;
    private int _hoverPixelX = -1, _hoverPixelY = -1;

    // ── Selection ───────────────────────────────────────────────────────
    private bool _selecting;
    private Point _selStart, _selEnd;
    private readonly HashSet<int> _selection = new();
    public IReadOnlyCollection<int> Selection => _selection;
    public event Action? SelectionChanged;

    // ── Coarsening levels ───────────────────────────────────────────────
    public int CurrentLevel => _currentLevel;
    public int LevelCount => _hierarchy?.LevelCount ?? 1;
    public event Action? LevelChanged;

    // ── Density grid (flat array, row-major) ────────────────────────────
    private int[] _density = Array.Empty<int>();
    private int _densityW, _densityH;
    private int _maxDensity;

    // ── Color ramp LUT (256 entries) ────────────────────────────────────
    private uint[] _colorLut = new uint[256];
    private string _colorRamp = "thermal";
    private bool _logScale = true;

    // ── SkiaSharp surface ───────────────────────────────────────────────
    private SKBitmap? _skBitmap;
    private int _skWidth, _skHeight;

    // ── Rendering area (excludes axis labels) ───────────────────────────
    private const int AxisMargin = 0; // we draw full-bleed for now

    // ── Visual settings (IGraphViewer) ──────────────────────────────────
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float NodeRadius { get; set; } = 6f;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float EdgeAlpha { get; set; } = 0.3f;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLabels { get; set; } = false;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowMinimap { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowEdges { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowNodes { get; set; } = true;

    // ── Color mode (IGraphViewer) ───────────────────────────────────────
    private string _nodeColorMode = "community";
    private string _edgeColorMode = "uniform";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string NodeColorMode
    {
        get => _nodeColorMode;
        set { _nodeColorMode = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EdgeColorMode
    {
        get => _edgeColorMode;
        set { _edgeColorMode = value; Invalidate(); }
    }

    // ── Performance instrumentation ─────────────────────────────────────
    private readonly Stopwatch _sw = new();
    private double _avgRenderMs, _avgBlitMs;
    private int _frameCount;
    private const double PerfAlpha = 0.1;

    // ══════════════════════════════════════════════════════════════════════
    // Constructor
    // ══════════════════════════════════════════════════════════════════════

    public MatrixView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        BuildColorLut();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Graph data management
    // ══════════════════════════════════════════════════════════════════════

    public void SetGraph(GraphModel graph)
    {
        _graph = graph;
        _hierarchy = null;
        _currentLevel = 0;
        _selection.Clear();
        ComputeOrdering();
        AutoFit();
        Invalidate();
    }

    public void SetGraphWithHierarchy(GraphModel graph, CoarseningHierarchy hierarchy)
    {
        _graph = graph;
        _hierarchy = hierarchy;
        _currentLevel = 0;
        _selection.Clear();
        ComputeOrdering();
        AutoFit();
        Invalidate();
    }

    public GraphModel? GetGraph() => _graph;
    public CoarseningHierarchy? GetHierarchy() => _hierarchy;

    // ══════════════════════════════════════════════════════════════════════
    // Node ordering
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Supported ordering modes: "original", "community", "degree", "bfs",
    /// or any NodeColorProvider metric name.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string OrderingMode
    {
        get => _orderingMode;
        set
        {
            if (_orderingMode == value) return;
            _orderingMode = value;
            ComputeOrdering();
            InvalidateDensity();
            Invalidate();
        }
    }

    /// <summary>Whether to use log-scale density mapping.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LogScale
    {
        get => _logScale;
        set { _logScale = value; Invalidate(); }
    }

    /// <summary>Color ramp name: "thermal", "viridis", "grayscale", "cyan".</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ColorRamp
    {
        get => _colorRamp;
        set
        {
            if (_colorRamp == value) return;
            _colorRamp = value;
            BuildColorLut();
            Invalidate();
        }
    }

    private void ComputeOrdering()
    {
        if (_graph == null || _graph.NodeCount == 0)
        {
            _ordering = Array.Empty<int>();
            _inverseOrder = Array.Empty<int>();
            return;
        }

        int n = _graph.NodeCount;
        _ordering = new int[n];

        switch (_orderingMode)
        {
            case "original":
                for (int i = 0; i < n; i++) _ordering[i] = i;
                break;

            case "community":
                // Group by community, then by degree within each community
                _ordering = Enumerable.Range(0, n)
                    .OrderBy(i => _graph.Community[i])
                    .ThenByDescending(i => _graph.Degree[i])
                    .ToArray();
                break;

            case "degree":
                _ordering = Enumerable.Range(0, n)
                    .OrderByDescending(i => _graph.Degree[i])
                    .ToArray();
                break;

            case "bfs":
                _ordering = BfsOrdering(_graph);
                break;

            case "spectral":
                var spectralOrder = SpectralAnalysis.SpectralOrdering(_graph);
                if (spectralOrder != null)
                    _ordering = spectralOrder;
                else
                    for (int i = 0; i < n; i++) _ordering[i] = i;
                break;

            default:
                // Try as a metric name (pagerank, betweenness, etc.)
                try
                {
                    var provider = new NodeColorProvider();
                    provider.SetGraph(_graph);
                    double[] values = provider.ComputeNodeMetric(_orderingMode);
                    _ordering = Enumerable.Range(0, n)
                        .OrderByDescending(i => values[i])
                        .ToArray();
                }
                catch
                {
                    // Fallback to original
                    for (int i = 0; i < n; i++) _ordering[i] = i;
                }
                break;
        }

        // Build inverse mapping
        _inverseOrder = new int[n];
        for (int i = 0; i < n; i++)
            _inverseOrder[_ordering[i]] = i;
    }

    private static int[] BfsOrdering(GraphModel graph)
    {
        int n = graph.NodeCount;
        var order = new int[n];
        var visited = new bool[n];
        int pos = 0;

        // Start from highest-degree node
        int start = 0;
        for (int i = 1; i < n; i++)
            if (graph.Degree[i] > graph.Degree[start]) start = i;

        var queue = new Queue<int>();
        queue.Enqueue(start);
        visited[start] = true;

        while (pos < n)
        {
            if (queue.Count > 0)
            {
                int node = queue.Dequeue();
                order[pos++] = node;
                var neighbors = graph.Neighbors(node);
                foreach (int nb in neighbors)
                {
                    if (!visited[nb])
                    {
                        visited[nb] = true;
                        queue.Enqueue(nb);
                    }
                }
            }
            else
            {
                // Disconnected component — find next unvisited
                for (int i = 0; i < n; i++)
                {
                    if (!visited[i])
                    {
                        visited[i] = true;
                        queue.Enqueue(i);
                        break;
                    }
                }
            }
        }
        return order;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Viewport management
    // ══════════════════════════════════════════════════════════════════════

    public void AutoFit()
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;
        _viewX = 0;
        _viewY = 0;
        _viewW = n;
        _viewH = n;
        InvalidateDensity();
    }

    /// <summary>
    /// Convert a screen pixel (in the data area) to matrix row/col coordinates.
    /// </summary>
    private (double col, double row) ScreenToMatrix(int px, int py)
    {
        int dw = DataWidth;
        int dh = DataHeight;
        if (dw <= 0 || dh <= 0) return (-1, -1);
        double col = _viewX + (double)px / dw * _viewW;
        double row = _viewY + (double)py / dh * _viewH;
        return (col, row);
    }

    private int DataWidth => Math.Max(1, Width - AxisMargin);
    private int DataHeight => Math.Max(1, Height - AxisMargin);

    // ══════════════════════════════════════════════════════════════════════
    // Density grid computation
    // ══════════════════════════════════════════════════════════════════════

    private bool _densityDirty = true;

    private void InvalidateDensity()
    {
        _densityDirty = true;
    }

    /// <summary>
    /// Recompute the density heatmap grid. Scans all edges and bins them
    /// into a pixelW × pixelH grid based on the current viewport and ordering.
    /// Uses flat int[] with manual row-major striding.
    /// </summary>
    private void RecomputeDensity()
    {
        if (_graph == null) return;
        int dw = DataWidth;
        int dh = DataHeight;
        if (dw <= 0 || dh <= 0) return;

        // Resize density buffer if needed
        int totalPixels = dw * dh;
        if (_density.Length < totalPixels)
            _density = new int[totalPixels];
        else
            Array.Clear(_density, 0, totalPixels);

        _densityW = dw;
        _densityH = dh;
        _maxDensity = 0;

        int n = _graph.NodeCount;
        int m = _graph.EdgeCount;
        double scaleX = dw / _viewW;
        double scaleY = dh / _viewH;

        // Scan all edges and accumulate density
        for (int e = 0; e < m; e++)
        {
            int srcNode = _graph.EdgeSource[e];
            int tgtNode = _graph.EdgeTarget[e];

            // Map to visual positions via ordering
            int srcVis = _inverseOrder[srcNode];
            int tgtVis = _inverseOrder[tgtNode];

            // Map to pixel coordinates
            int px = (int)((tgtVis - _viewX) * scaleX);
            int py = (int)((srcVis - _viewY) * scaleY);

            if (px >= 0 && px < dw && py >= 0 && py < dh)
            {
                int idx = py * dw + px;
                int val = ++_density[idx];
                if (val > _maxDensity) _maxDensity = val;
            }

            // For undirected graphs, also plot the symmetric entry
            if (!_graph.IsDirected)
            {
                int px2 = (int)((srcVis - _viewX) * scaleX);
                int py2 = (int)((tgtVis - _viewY) * scaleY);
                if (px2 >= 0 && px2 < dw && py2 >= 0 && py2 < dh)
                {
                    int idx2 = py2 * dw + px2;
                    int val2 = ++_density[idx2];
                    if (val2 > _maxDensity) _maxDensity = val2;
                }
            }
        }

        _densityDirty = false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Color ramp
    // ══════════════════════════════════════════════════════════════════════

    private void BuildColorLut()
    {
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            byte r, g, b;

            switch (_colorRamp)
            {
                case "viridis":
                    // Approximate viridis: purple → teal → yellow
                    r = (byte)(68 + t * (253 - 68));
                    g = (byte)(1 + t * (231 - 1));
                    b = (byte)(84 + (t < 0.5 ? t * 2 * (158 - 84) : (158 - (t - 0.5) * 2 * 158)));
                    break;

                case "grayscale":
                    r = g = b = (byte)i;
                    break;

                case "cyan":
                    r = 0;
                    g = (byte)(i * 0.9);
                    b = (byte)i;
                    break;

                case "thermal":
                default:
                    // Black → dark blue → cyan → yellow → white
                    if (t < 0.25)
                    {
                        double s = t / 0.25;
                        r = 0; g = 0; b = (byte)(s * 180);
                    }
                    else if (t < 0.5)
                    {
                        double s = (t - 0.25) / 0.25;
                        r = 0; g = (byte)(s * 220); b = (byte)(180 + s * 55);
                    }
                    else if (t < 0.75)
                    {
                        double s = (t - 0.5) / 0.25;
                        r = (byte)(s * 255); g = (byte)(220 + s * 35); b = (byte)(235 - s * 235);
                    }
                    else
                    {
                        double s = (t - 0.75) / 0.25;
                        r = 255; g = 255; b = (byte)(s * 255);
                    }
                    break;
            }

            // Store as BGRA (SkiaSharp native format)
            _colorLut[i] = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
        }
    }

    private uint DensityToColor(int density)
    {
        if (density <= 0) return 0xFF000000; // black
        if (_maxDensity <= 0) return 0xFF000000;

        double t;
        if (_logScale)
            t = Math.Log(1 + density) / Math.Log(1 + _maxDensity);
        else
            t = (double)density / _maxDensity;

        int idx = Math.Clamp((int)(t * 255), 0, 255);
        return _colorLut[idx];
    }

    // ══════════════════════════════════════════════════════════════════════
    // SkiaSharp surface management
    // ══════════════════════════════════════════════════════════════════════

    private void EnsureSkiaSurface(int w, int h)
    {
        if (_skBitmap != null && _skWidth == w && _skHeight == h) return;
        _skBitmap?.Dispose();
        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skWidth = w;
        _skHeight = h;
    }

    // ══════════════════════════════════════════════════════════════════════
    // RENDERING
    // ══════════════════════════════════════════════════════════════════════

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_graph == null || Width <= 0 || Height <= 0)
        {
            e.Graphics.Clear(Color.Black);
            DrawEmptyHint(e.Graphics);
            return;
        }

        _sw.Restart();

        int w = Width;
        int h = Height;
        EnsureSkiaSurface(w, h);
        if (_skBitmap == null) return;

        // Recompute density if viewport changed
        if (_densityDirty || _densityW != DataWidth || _densityH != DataHeight)
            RecomputeDensity();

        // ── Write density heatmap directly to bitmap pixels ─────────
        int dw = DataWidth;
        int dh = DataHeight;
        var pixels = _skBitmap.GetPixels();

        unsafe
        {
            uint* ptr = (uint*)pixels.ToPointer();

            for (int row = 0; row < dh && row < h; row++)
            {
                int densityRowOff = row * _densityW;
                int bitmapRowOff = row * w;
                for (int col = 0; col < dw && col < w; col++)
                {
                    ptr[bitmapRowOff + col] = DensityToColor(_density[densityRowOff + col]);
                }
            }
        }

        // ── Draw overlays via SKCanvas ──────────────────────────────
        using var canvas = new SKCanvas(_skBitmap);
        // Don't clear — we wrote the pixel data directly above

        // Diagonal reference line (for undirected graphs)
        if (!_graph.IsDirected)
        {
            DrawDiagonal(canvas, dw, dh);
        }

        // Community boundaries
        DrawCommunityBoundaries(canvas, dw, dh);

        // Selection highlight
        DrawSelectionHighlight(canvas, dw, dh);

        // Crosshair at mouse position
        if (_hoverPixelX >= 0 && _hoverPixelY >= 0)
        {
            DrawCrosshair(canvas, w, h, dw, dh);
        }

        // Minimap
        if (ShowMinimap && IsZoomed())
        {
            DrawMinimap(canvas, w, h);
        }

        // HUD text
        DrawHud(canvas, w, h);

        double renderMs = _sw.Elapsed.TotalMilliseconds;

        // ── Blit to WinForms ────────────────────────────────────────
        _sw.Restart();
        using var img = SKImage.FromBitmap(_skBitmap);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);

        // Use a faster blit: pin the SKBitmap pixels and wrap in a GDI+ Bitmap
        var info = _skBitmap.Info;
        using var gdi = new System.Drawing.Bitmap(
            info.Width, info.Height, info.RowBytes,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb,
            _skBitmap.GetPixels());
        e.Graphics.DrawImage(gdi, 0, 0);

        double blitMs = _sw.Elapsed.TotalMilliseconds;

        _frameCount++;
        UpdateEma(ref _avgRenderMs, renderMs);
        UpdateEma(ref _avgBlitMs, blitMs);
    }

    private void DrawEmptyHint(Graphics g)
    {
        string hint = "Adjacency Matrix View\nLoad a graph to visualize its structure";
        using var font = new Font("Segoe UI", 14f);
        using var brush = new SolidBrush(Color.FromArgb(100, 200, 200, 200));
        var size = g.MeasureString(hint, font);
        g.DrawString(hint, font, brush,
            (Width - size.Width) / 2, (Height - size.Height) / 2);
    }

    private void DrawDiagonal(SKCanvas canvas, int dw, int dh)
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;

        // Map diagonal endpoints from matrix coords to screen coords
        float x0 = (float)((0 - _viewX) / _viewW * dw);
        float y0 = (float)((0 - _viewY) / _viewH * dh);
        float x1 = (float)((n - _viewX) / _viewW * dw);
        float y1 = (float)((n - _viewY) / _viewH * dh);

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 30),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(x0, y0, x1, y1, paint);
    }

    private void DrawCommunityBoundaries(SKCanvas canvas, int dw, int dh)
    {
        if (_graph == null || _orderingMode != "community") return;

        // Find community transitions in the visual ordering
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 25),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };

        int lastComm = -1;
        for (int vi = 0; vi < _ordering.Length; vi++)
        {
            int nodeIdx = _ordering[vi];
            int comm = _graph.Community[nodeIdx];
            if (comm != lastComm && lastComm >= 0)
            {
                // Draw boundary lines at this visual position
                float px = (float)((vi - _viewX) / _viewW * dw);
                float py = (float)((vi - _viewY) / _viewH * dh);
                canvas.DrawLine(px, 0, px, dh, paint);
                canvas.DrawLine(0, py, dw, py, paint);
            }
            lastComm = comm;
        }
    }

    private void DrawSelectionHighlight(SKCanvas canvas, int dw, int dh)
    {
        if (_selection.Count == 0 || _graph == null) return;

        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 100, 60),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };

        double scaleX = dw / _viewW;
        double scaleY = dh / _viewH;

        foreach (int nodeIdx in _selection)
        {
            if (nodeIdx >= _inverseOrder.Length) continue;
            int vis = _inverseOrder[nodeIdx];
            float px = (float)((vis - _viewX) * scaleX);
            float py = (float)((vis - _viewY) * scaleY);

            // Highlight row and column
            canvas.DrawLine(0, py, dw, py, paint);
            canvas.DrawLine(px, 0, px, dh, paint);
        }
    }

    private void DrawCrosshair(SKCanvas canvas, int w, int h, int dw, int dh)
    {
        int mx = _hoverPixelX;
        int my = _hoverPixelY;

        // ── Subtle full-span crosshair ──────────────────────────────
        using var subtlePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 30),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(mx, 0, mx, h, subtlePaint);
        canvas.DrawLine(0, my, w, my, subtlePaint);

        // ── Find which node rows/cols the mouse covers ──────────────
        var (col, row) = ScreenToMatrix(mx, my);
        int matRow = (int)Math.Floor(row);
        int matCol = (int)Math.Floor(col);
        if (matRow < 0 || matRow >= _graph!.NodeCount ||
            matCol < 0 || matCol >= _graph.NodeCount) return;

        // The "from" node (row) and "to" node (column)
        int fromNode = _ordering[matRow];
        int toNode = _ordering[matCol];

        // ── Bright highlight on filled pixels in this row & column ──
        double scaleX = (double)dw / _viewW;
        double scaleY = (double)dh / _viewH;

        using var brightPaint = new SKPaint
        {
            Color = new SKColor(0, 220, 255, 220),
            StrokeWidth = 1,
            IsAntialias = false,
            Style = SKPaintStyle.Fill,
        };

        // Highlight filled pixels in the hovered row (fromNode's connections)
        var fromNeighbors = _graph.Neighbors(fromNode);
        float pixSize = Math.Max(1f, (float)(1.0 / _viewW * dw));

        foreach (int nb in fromNeighbors)
        {
            if (nb >= _inverseOrder.Length) continue;
            int nbVis = _inverseOrder[nb];
            float px = (float)((nbVis - _viewX) * scaleX);
            float py = (float)((matRow - _viewY) * scaleY);
            if (px >= 0 && px < dw && py >= 0 && py < dh)
            {
                canvas.DrawRect(px, py, pixSize, pixSize, brightPaint);
            }
        }

        // Highlight filled pixels in the hovered column (toNode's connections)
        var toNeighbors = _graph.Neighbors(toNode);
        foreach (int nb in toNeighbors)
        {
            if (nb >= _inverseOrder.Length) continue;
            int nbVis = _inverseOrder[nb];
            float px = (float)((matCol - _viewX) * scaleX);
            float py = (float)((nbVis - _viewY) * scaleY);
            if (px >= 0 && px < dw && py >= 0 && py < dh)
            {
                canvas.DrawRect(px, py, pixSize, pixSize, brightPaint);
            }
        }

        // ── Tooltip text ────────────────────────────────────────────
        string fromLabel = fromNode < _graph.Labels.Length ? _graph.Labels[fromNode] : fromNode.ToString();
        string toLabel = toNode < _graph.Labels.Length ? _graph.Labels[toNode] : toNode.ToString();
        string tipText = $"Row: {fromLabel} (deg {_graph.Degree[fromNode]})  Col: {toLabel} (deg {_graph.Degree[toNode]})";

        using var tipFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12f);
        using var tipPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
        };
        using var tipBg = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 200),
            Style = SKPaintStyle.Fill,
        };

        float tw = tipFont.MeasureText(tipText);
        float tx = Math.Min(mx + 12, w - tw - 8);
        float ty = Math.Max(my - 8, 18);

        canvas.DrawRect(tx - 4, ty - 14, tw + 8, 18, tipBg);
        canvas.DrawText(tipText, tx, ty, SKTextAlign.Left, tipFont, tipPaint);
    }

    private const int MinimapSize = 140;
    private const int MinimapMargin = 8;

    private bool IsZoomed()
    {
        if (_graph == null) return false;
        int n = _graph.NodeCount;
        return _viewW < n * 0.99 || _viewH < n * 0.99;
    }

    private void DrawMinimap(SKCanvas canvas, int w, int h)
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;
        if (n == 0) return;

        int mmSize = Math.Min(MinimapSize, Math.Min(w, h) / 4);
        int mmX = w - mmSize - MinimapMargin;
        int mmY = h - mmSize - MinimapMargin;

        // Background
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRect(mmX, mmY, mmSize, mmSize, bgPaint);

        // Border
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(100, 100, 100, 200),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };
        canvas.DrawRect(mmX, mmY, mmSize, mmSize, borderPaint);

        // Draw a micro density map (simplified: just plot non-zero bins)
        // Scale down the full matrix into the minimap
        int m = _graph.EdgeCount;
        double mmScale = (double)mmSize / n;

        using var dotPaint = new SKPaint
        {
            Color = new SKColor(100, 180, 255, 150),
            StrokeWidth = 1,
            StrokeCap = SKStrokeCap.Square,
            Style = SKPaintStyle.Stroke,
        };

        for (int e = 0; e < m; e++)
        {
            int srcVis = _inverseOrder[_graph.EdgeSource[e]];
            int tgtVis = _inverseOrder[_graph.EdgeTarget[e]];
            int px = (int)(mmX + tgtVis * mmScale);
            int py = (int)(mmY + srcVis * mmScale);
            if (px >= mmX && px < mmX + mmSize && py >= mmY && py < mmY + mmSize)
                canvas.DrawPoint(px, py, dotPaint);
        }

        // Draw viewport rectangle
        float vx = (float)(mmX + _viewX / n * mmSize);
        float vy = (float)(mmY + _viewY / n * mmSize);
        float vw = (float)(_viewW / n * mmSize);
        float vh = (float)(_viewH / n * mmSize);

        using var vpPaint = new SKPaint
        {
            Color = new SKColor(255, 200, 50, 200),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
        };
        canvas.DrawRect(vx, vy, vw, vh, vpPaint);
    }

    private void DrawHud(SKCanvas canvas, int w, int h)
    {
        if (_graph == null) return;

        string status = $"{_graph.NodeCount}n × {_graph.NodeCount}n  |  {_graph.EdgeCount} edges  |  {_orderingMode}";
        if (IsZoomed())
        {
            int visRows = (int)_viewH;
            status += $"  |  viewing {visRows}×{(int)_viewW}";
        }
        status += $"  |  {_avgRenderMs:F1}ms + {_avgBlitMs:F1}ms blit";

        using var statusFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12f);
        using var paint = new SKPaint
        {
            Color = new SKColor(200, 200, 200, 200),
            IsAntialias = true,
        };
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            Style = SKPaintStyle.Fill,
        };

        float tw = statusFont.MeasureText(status);
        canvas.DrawRect(6, h - 22, tw + 8, 18, bgPaint);
        canvas.DrawText(status, 10, h - 8, SKTextAlign.Left, statusFont, paint);
    }

    private void UpdateEma(ref double avg, double sample)
    {
        if (_frameCount <= 1) avg = sample;
        else avg = avg * (1 - PerfAlpha) + sample * PerfAlpha;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Mouse interaction
    // ══════════════════════════════════════════════════════════════════════

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Button is MouseButtons.Middle or MouseButtons.Right)
        {
            _panning = true;
            _lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
        }
        else if (e.Button == MouseButtons.Left)
        {
            _selecting = true;
            _selStart = _selEnd = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_panning)
        {
            int dx = e.X - _lastMouse.X;
            int dy = e.Y - _lastMouse.Y;

            // Convert pixel delta to matrix coord delta
            _viewX -= dx * _viewW / DataWidth;
            _viewY -= dy * _viewH / DataHeight;
            ClampView();
            _lastMouse = e.Location;
            InvalidateDensity();
            Invalidate();
        }
        else if (_selecting)
        {
            _selEnd = e.Location;
            Invalidate();
        }
        else
        {
            _hoverPixelX = e.X;
            _hoverPixelY = e.Y;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_panning && e.Button is MouseButtons.Middle or MouseButtons.Right)
        {
            _panning = false;
            Cursor = Cursors.Default;
        }
        else if (_selecting && e.Button == MouseButtons.Left)
        {
            _selecting = false;

            // If it was a drag (not just a click), zoom to that region
            int dx = Math.Abs(_selEnd.X - _selStart.X);
            int dy = Math.Abs(_selEnd.Y - _selStart.Y);

            if (dx > 5 && dy > 5)
            {
                // Rubber-band zoom
                int x1 = Math.Min(_selStart.X, _selEnd.X);
                int y1 = Math.Min(_selStart.Y, _selEnd.Y);
                int x2 = Math.Max(_selStart.X, _selEnd.X);
                int y2 = Math.Max(_selStart.Y, _selEnd.Y);

                var (col1, row1) = ScreenToMatrix(x1, y1);
                var (col2, row2) = ScreenToMatrix(x2, y2);

                _viewX = col1;
                _viewY = row1;
                _viewW = col2 - col1;
                _viewH = row2 - row1;
                ClampView();
                InvalidateDensity();
                Invalidate();
            }
            else if (dx < 3 && dy < 3)
            {
                // Click — select the node at this position
                HandleClick(e.X, e.Y, (ModifierKeys & Keys.Control) != 0);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPixelX = -1;
        _hoverPixelY = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_graph == null) return;

        double factor = e.Delta > 0 ? 0.8 : 1.25;

        // Zoom centered on mouse position
        var (col, row) = ScreenToMatrix(e.X, e.Y);

        double newW = _viewW * factor;
        double newH = _viewH * factor;

        // Don't zoom out beyond full matrix
        int n = _graph.NodeCount;
        newW = Math.Min(newW, n);
        newH = Math.Min(newH, n);

        // Don't zoom in beyond individual cells
        newW = Math.Max(newW, Math.Min(20, n));
        newH = Math.Max(newH, Math.Min(20, n));

        // Adjust origin to keep mouse position stable
        double pctX = (col - _viewX) / _viewW;
        double pctY = (row - _viewY) / _viewH;
        _viewX = col - pctX * newW;
        _viewY = row - pctY * newH;
        _viewW = newW;
        _viewH = newH;

        ClampView();
        InvalidateDensity();
        Invalidate();
    }

    private void ClampView()
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;
        _viewW = Math.Max(1, Math.Min(_viewW, n));
        _viewH = Math.Max(1, Math.Min(_viewH, n));
        _viewX = Math.Max(0, Math.Min(_viewX, n - _viewW));
        _viewY = Math.Max(0, Math.Min(_viewY, n - _viewH));
    }

    private void HandleClick(int px, int py, bool additive)
    {
        if (_graph == null) return;
        var (col, row) = ScreenToMatrix(px, py);
        int matRow = (int)Math.Floor(row);
        if (matRow < 0 || matRow >= _graph.NodeCount) return;

        int nodeIdx = _ordering[matRow];
        if (!additive)
            _selection.Clear();

        if (_selection.Contains(nodeIdx))
            _selection.Remove(nodeIdx);
        else
            _selection.Add(nodeIdx);

        Invalidate();
        SelectionChanged?.Invoke();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Keyboard
    // ══════════════════════════════════════════════════════════════════════

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Home:
            case Keys.F:
                AutoFit();
                Invalidate();
                e.Handled = true;
                break;

            case Keys.Escape:
                ClearSelection();
                e.Handled = true;
                break;

            case Keys.PageUp:
                GoCoarser();
                e.Handled = true;
                break;

            case Keys.PageDown:
                GoFiner();
                e.Handled = true;
                break;

            case Keys.Oemplus:
            case Keys.Add:
                // Zoom in centered
                ZoomCenter(0.8);
                e.Handled = true;
                break;

            case Keys.OemMinus:
            case Keys.Subtract:
                // Zoom out centered
                ZoomCenter(1.25);
                e.Handled = true;
                break;

            case Keys.Left:
                Pan(-0.1, 0);
                e.Handled = true;
                break;
            case Keys.Right:
                Pan(0.1, 0);
                e.Handled = true;
                break;
            case Keys.Up:
                Pan(0, -0.1);
                e.Handled = true;
                break;
            case Keys.Down:
                Pan(0, 0.1);
                e.Handled = true;
                break;
        }
    }

    private void ZoomCenter(double factor)
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;

        double cx = _viewX + _viewW / 2;
        double cy = _viewY + _viewH / 2;
        double newW = Math.Clamp(_viewW * factor, Math.Min(20, n), n);
        double newH = Math.Clamp(_viewH * factor, Math.Min(20, n), n);
        _viewX = cx - newW / 2;
        _viewY = cy - newH / 2;
        _viewW = newW;
        _viewH = newH;
        ClampView();
        InvalidateDensity();
        Invalidate();
    }

    private void Pan(double dxFraction, double dyFraction)
    {
        _viewX += _viewW * dxFraction;
        _viewY += _viewH * dyFraction;
        ClampView();
        InvalidateDensity();
        Invalidate();
    }

    // ══════════════════════════════════════════════════════════════════════
    // IGraphViewer — Selection
    // ══════════════════════════════════════════════════════════════════════

    public void ClearSelection()
    {
        if (_selection.Count > 0)
        {
            _selection.Clear();
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    public void SetSelection(IEnumerable<int> indices)
    {
        _selection.Clear();
        int max = _graph?.NodeCount ?? 0;
        foreach (int i in indices)
            if (i >= 0 && i < max) _selection.Add(i);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        if (_graph == null) return;
        _selection.Clear();
        for (int i = 0; i < _graph.NodeCount; i++) _selection.Add(i);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    // ══════════════════════════════════════════════════════════════════════
    // IGraphViewer — Coarsening levels
    // ══════════════════════════════════════════════════════════════════════

    public bool SetLevel(int level)
    {
        if (_hierarchy == null || level < 0 || level >= _hierarchy.LevelCount)
            return false;
        if (level == _currentLevel) return true;

        _currentLevel = level;
        _graph = _hierarchy.Graphs[level];
        _selection.Clear();
        ComputeOrdering();
        AutoFit();
        Invalidate();
        LevelChanged?.Invoke();
        return true;
    }

    public void GoCoarser()
    {
        if (_hierarchy == null) return;
        int target = _currentLevel + 1;
        if (target >= _hierarchy.LevelCount) return;
        SetLevel(target);
    }

    public void GoFiner()
    {
        if (_hierarchy == null) return;
        int target = _currentLevel - 1;
        if (target < 0) return;
        SetLevel(target);
    }

    // ══════════════════════════════════════════════════════════════════════
    // IGraphViewer — Performance
    // ══════════════════════════════════════════════════════════════════════

    public Dictionary<string, object> GetRenderStats()
    {
        return new Dictionary<string, object>
        {
            ["viewer_type"] = "matrix",
            ["avg_render_ms"] = Math.Round(_avgRenderMs, 2),
            ["avg_blit_ms"] = Math.Round(_avgBlitMs, 2),
            ["frame_count"] = _frameCount,
            ["density_grid"] = $"{_densityW}x{_densityH}",
            ["max_density"] = _maxDensity,
            ["ordering"] = _orderingMode,
            ["log_scale"] = _logScale,
            ["color_ramp"] = _colorRamp,
        };
    }

    public void ResetRenderStats()
    {
        _avgRenderMs = 0;
        _avgBlitMs = 0;
        _frameCount = 0;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Dispose
    // ══════════════════════════════════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _skBitmap?.Dispose();
            _skBitmap = null;
        }
        base.Dispose(disposing);
    }
}
