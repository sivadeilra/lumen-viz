using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LumenViz;

/// <summary>
/// Custom WinForms control that renders a graph with force-directed layout.
/// Supports zoom, pan, community coloring, node labels, rubber-band
/// node selection, coarsening level navigation with animated transitions,
/// and parent-child relationship highlighting between adjacent levels.
/// </summary>
public class GraphView : Control
{
    private GraphModel? _graph;
    private PointF _pan = PointF.Empty;
    private float _zoom = 1.0f;
    private Point _lastMouse;
    private bool _panning;

    // ── Selection ───────────────────────────────────────────────────────
    private bool _selecting;
    private Point _selStart;
    private Point _selEnd;
    private readonly HashSet<int> _selection = new();

    /// <summary>Currently selected node indices (read-only view).</summary>
    public IReadOnlyCollection<int> Selection => _selection;

    /// <summary>Fired when the selection changes.</summary>
    public event Action? SelectionChanged;

    // ── Coarsening hierarchy ────────────────────────────────────────────
    private CoarseningHierarchy? _hierarchy;
    private int _currentLevel;

    /// <summary>Current coarsening level (0 = finest/original).</summary>
    public int CurrentLevel => _currentLevel;

    /// <summary>Total number of coarsening levels (1 if no hierarchy).</summary>
    public int LevelCount => _hierarchy?.LevelCount ?? 1;

    /// <summary>Fired when the coarsening level changes.</summary>
    public event Action? LevelChanged;

    // ── Animation ───────────────────────────────────────────────────────
    private System.Windows.Forms.Timer? _animTimer;
    private double[]? _animFromX, _animFromY;  // start positions
    private double[]? _animToX, _animToY;      // target positions
    private int[]? _animMapping;               // old→new index mapping
    private int _animFrame;
    private const int AnimFrames = 20;
    private const int AnimIntervalMs = 16; // ~60 fps
    private bool _animatingUp; // true = coarsening, false = refining
    private int _animTargetLevel;

    // ── Visual settings ─────────────────────────────────────────────────
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float NodeRadius { get; set; } = 6f;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float EdgeAlpha { get; set; } = 0.3f;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLabels { get; set; } = false;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AntiAlias { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BackgroundColor { get; set; } = Color.FromArgb(24, 24, 32);

    /// <summary>Show parent-child relationships to next coarser level.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowParentHighlight { get; set; } = true;

    /// <summary>
    /// Community palette — distinguishable, saturated colors that look good
    /// on a dark background.
    /// </summary>
    private static readonly Color[] Palette = new[]
    {
        Color.FromArgb(102, 194, 255),  // sky blue
        Color.FromArgb(255, 128, 102),  // coral
        Color.FromArgb(128, 230, 128),  // leaf green
        Color.FromArgb(255, 204, 77),   // amber
        Color.FromArgb(204, 153, 255),  // lavender
        Color.FromArgb(255, 153, 204),  // pink
        Color.FromArgb(102, 230, 204),  // teal
        Color.FromArgb(255, 179, 102),  // orange
        Color.FromArgb(153, 204, 255),  // powder blue
        Color.FromArgb(230, 230, 128),  // lime
        Color.FromArgb(204, 128, 255),  // violet
        Color.FromArgb(128, 204, 179),  // sage
    };

    /// <summary>Pre-cached palette brushes (created once).</summary>
    private readonly SolidBrush[] _paletteBrushes;

    public GraphView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.Selectable, true);
        BackColor = BackgroundColor;

        _paletteBrushes = new SolidBrush[Palette.Length];
        for (int i = 0; i < Palette.Length; i++)
            _paletteBrushes[i] = new SolidBrush(Palette[i]);
    }

    // ── Graph / hierarchy management ────────────────────────────────────

    /// <summary>
    /// Load a graph model without coarsening hierarchy.
    /// </summary>
    public void SetGraph(GraphModel graph)
    {
        _graph = graph;
        _hierarchy = null;
        _currentLevel = 0;
        _selection.Clear();
        StopAnimation();
        AutoFit();
        Invalidate();
    }

    /// <summary>
    /// Load a graph with its coarsening hierarchy for level navigation.
    /// </summary>
    public void SetGraphWithHierarchy(GraphModel graph, CoarseningHierarchy hierarchy)
    {
        _graph = graph;
        _hierarchy = hierarchy;
        _currentLevel = 0;
        _selection.Clear();
        StopAnimation();
        AutoFit();
        Invalidate();
        LevelChanged?.Invoke();
    }

    public GraphModel? GetGraph() => _graph;
    public CoarseningHierarchy? GetHierarchy() => _hierarchy;

    // ── Level navigation ────────────────────────────────────────────────

    /// <summary>Navigate to a coarser level (up). Returns true if changed.</summary>
    public bool GoCoarser()
    {
        if (_hierarchy == null || _currentLevel >= _hierarchy.LevelCount - 1)
            return false;
        AnimateToLevel(_currentLevel + 1);
        return true;
    }

    /// <summary>Navigate to a finer level (down). Returns true if changed.</summary>
    public bool GoFiner()
    {
        if (_hierarchy == null || _currentLevel <= 0)
            return false;
        AnimateToLevel(_currentLevel - 1);
        return true;
    }

    /// <summary>Jump to a specific level (0 = finest).</summary>
    public bool SetLevel(int level)
    {
        if (_hierarchy == null || level < 0 || level >= _hierarchy.LevelCount)
            return false;
        if (level == _currentLevel) return true;
        AnimateToLevel(level);
        return true;
    }

    // ── Animation ───────────────────────────────────────────────────────

    private void AnimateToLevel(int targetLevel)
    {
        if (_hierarchy == null) return;
        StopAnimation();

        var fromGraph = _hierarchy.Graphs[_currentLevel];
        var toGraph = _hierarchy.Graphs[targetLevel];
        bool goingUp = targetLevel > _currentLevel;

        _animatingUp = goingUp;
        _animTargetLevel = targetLevel;

        if (goingUp)
        {
            // Coarsening: each fine node moves to its coarse parent's position
            // Mapping: for each node in fromGraph, where does it go in toGraph?
            int fromN = fromGraph.NodeCount;
            _animFromX = new double[fromN];
            _animFromY = new double[fromN];
            _animToX = new double[fromN];
            _animToY = new double[fromN];
            _animMapping = new int[fromN]; // fine→coarse

            Array.Copy(fromGraph.NodeX, _animFromX, fromN);
            Array.Copy(fromGraph.NodeY, _animFromY, fromN);

            // Build composite mapping from current level to target level
            for (int i = 0; i < fromN; i++)
            {
                int coarseIdx = _hierarchy.MapUp(i, _currentLevel, targetLevel);
                _animMapping[i] = coarseIdx;
                _animToX[i] = toGraph.NodeX[coarseIdx];
                _animToY[i] = toGraph.NodeY[coarseIdx];
            }
        }
        else
        {
            // Refining: each coarse node expands to its children's positions
            // We animate from the coarse graph, and at the end switch to fine
            int fromN = fromGraph.NodeCount;
            _animFromX = new double[fromN];
            _animFromY = new double[fromN];
            _animToX = new double[fromN];
            _animToY = new double[fromN];
            _animMapping = null; // not needed for rendering — we lerp in-place

            Array.Copy(fromGraph.NodeX, _animFromX, fromN);
            Array.Copy(fromGraph.NodeY, _animFromY, fromN);

            // Target: position each current-level node at where its children are in the fine graph
            // But we can't really expand N→M during animation easily.
            // Instead: morph current positions toward the centroid of their fine children.
            // At the end, snap to the fine graph.
            // Actually, let's do it from the fine side: animate the fine graph nodes
            // FROM the coarse parent positions TO their actual fine positions.
            int toN = toGraph.NodeCount;
            _animFromX = new double[toN];
            _animFromY = new double[toN];
            _animToX = new double[toN];
            _animToY = new double[toN];

            // For each fine node, start at coarse parent's position
            // The mapping for level targetLevel→targetLevel+1 ... _currentLevel
            for (int i = 0; i < toN; i++)
            {
                int coarseIdx = _hierarchy.MapUp(i, targetLevel, _currentLevel);
                _animFromX[i] = fromGraph.NodeX[coarseIdx];
                _animFromY[i] = fromGraph.NodeY[coarseIdx];
                _animToX[i] = toGraph.NodeX[i];
                _animToY[i] = toGraph.NodeY[i];
            }

            // Switch graph immediately to the fine graph (more nodes)
            // but start rendering at coarse positions
            _graph = toGraph;
        }

        _animFrame = 0;
        _animTimer = new System.Windows.Forms.Timer { Interval = AnimIntervalMs };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _animFrame++;
        if (_animFrame >= AnimFrames)
        {
            FinishAnimation();
            return;
        }

        // Ease-in-out interpolation (smoothstep)
        double t = (double)_animFrame / AnimFrames;
        t = t * t * (3.0 - 2.0 * t); // smoothstep

        if (_animatingUp)
        {
            // Lerp the current (fine) graph's positions toward coarse targets
            var graph = _hierarchy!.Graphs[_currentLevel];
            int n = graph.NodeCount;
            for (int i = 0; i < n; i++)
            {
                graph.NodeX[i] = _animFromX![i] + (_animToX![i] - _animFromX[i]) * t;
                graph.NodeY[i] = _animFromY![i] + (_animToY![i] - _animFromY[i]) * t;
            }
        }
        else
        {
            // Lerp the fine graph's positions from coarse parent toward actual
            var graph = _graph!;
            int n = Math.Min(graph.NodeCount, _animFromX!.Length);
            for (int i = 0; i < n; i++)
            {
                graph.NodeX[i] = _animFromX![i] + (_animToX![i] - _animFromX[i]) * t;
                graph.NodeY[i] = _animFromY![i] + (_animToY![i] - _animFromY[i]) * t;
            }
        }

        Invalidate();
    }

    private void FinishAnimation()
    {
        StopAnimation();

        // Set final positions and switch to target level
        _currentLevel = _animTargetLevel;
        _graph = _hierarchy!.Graphs[_currentLevel];

        // Restore exact target positions
        if (_animToX != null && _animToY != null)
        {
            int n = Math.Min(_graph.NodeCount, _animToX.Length);
            for (int i = 0; i < n; i++)
            {
                _graph.NodeX[i] = _animToX[i];
                _graph.NodeY[i] = _animToY[i];
            }
        }

        _selection.Clear();
        AutoFit();
        Invalidate();
        LevelChanged?.Invoke();
    }

    private void StopAnimation()
    {
        if (_animTimer != null)
        {
            _animTimer.Stop();
            _animTimer.Tick -= OnAnimTick;
            _animTimer.Dispose();
            _animTimer = null;
        }
        _animFromX = _animFromY = _animToX = _animToY = null;
        _animMapping = null;
    }

    // ── Auto-fit ────────────────────────────────────────────────────────

    /// <summary>
    /// Calculate zoom and pan so the graph fills ~80% of the control area.
    /// </summary>
    public void AutoFit()
    {
        if (_graph == null || _graph.NodeCount == 0) return;

        int n = _graph.NodeCount;
        var px = _graph.NodeX;
        var py = _graph.NodeY;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            if (px[i] < minX) minX = px[i];
            if (py[i] < minY) minY = py[i];
            if (px[i] > maxX) maxX = px[i];
            if (py[i] > maxY) maxY = py[i];
        }

        double graphW = maxX - minX;
        double graphH = maxY - minY;
        if (graphW < 1) graphW = 1;
        if (graphH < 1) graphH = 1;

        float margin = 40;
        float viewW = Width - margin * 2;
        float viewH = Height - margin * 2;
        if (viewW < 1) viewW = 1;
        if (viewH < 1) viewH = 1;

        _zoom = (float)Math.Min(viewW / graphW, viewH / graphH);
        _pan = new PointF(
            (float)(margin - minX * _zoom + (viewW - graphW * _zoom) / 2),
            (float)(margin - minY * _zoom + (viewH - graphH * _zoom) / 2)
        );
    }

    // ── Selection management ────────────────────────────────────────────

    public void ClearSelection()
    {
        if (_selection.Count > 0)
        {
            _selection.Clear();
            SelectionChanged?.Invoke();
            Invalidate();
        }
    }

    public void SelectAll()
    {
        if (_graph == null) return;
        _selection.Clear();
        for (int i = 0; i < _graph.NodeCount; i++)
            _selection.Add(i);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    // ── Painting ────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackgroundColor);

        if (_graph == null || _graph.NodeCount == 0)
        {
            DrawPlaceholder(g);
            return;
        }

        if (AntiAlias)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        int n = _graph.NodeCount;
        var px = _graph.NodeX;
        var py = _graph.NodeY;
        var degree = _graph.Degree;

        // ── Draw parent highlight (relationship to next coarser level) ──
        if (ShowParentHighlight && _hierarchy != null && _currentLevel < _hierarchy.LevelCount - 1 && _animTimer == null)
        {
            DrawParentHighlight(g);
        }

        // ── Draw edges ─────────────────────────────────────────────────
        int edgeAlpha = Math.Clamp((int)(EdgeAlpha * 255), 10, 255);
        using var edgePen = new Pen(Color.FromArgb(edgeAlpha, 120, 130, 150), 1f);

        var es = _graph.EdgeSource;
        var et = _graph.EdgeTarget;
        for (int ei = 0; ei < _graph.EdgeCount; ei++)
        {
            var a = WorldToScreen(px[es[ei]], py[es[ei]]);
            var b = WorldToScreen(px[et[ei]], py[et[ei]]);
            g.DrawLine(edgePen, a, b);
        }

        // ── Draw selected node glow ────────────────────────────────────
        if (_selection.Count > 0)
        {
            using var glowBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 100));
            float glowR = NodeRadius + 10;
            foreach (int i in _selection)
            {
                if (i >= n) continue;
                var pt = WorldToScreen(px[i], py[i]);
                g.FillEllipse(glowBrush, pt.X - glowR, pt.Y - glowR, glowR * 2, glowR * 2);
            }
        }

        // ── Draw nodes ─────────────────────────────────────────────────
        float r = NodeRadius;
        using var outlinePen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f);
        using var selOutlinePen = new Pen(Color.FromArgb(255, 255, 255, 100), 2f);

        var community = _graph.Community;
        for (int i = 0; i < n; i++)
        {
            var pt = WorldToScreen(px[i], py[i]);
            float nr = r + Math.Min(degree[i] * 0.3f, 6f);

            g.FillEllipse(_paletteBrushes[community[i] % Palette.Length],
                pt.X - nr, pt.Y - nr, nr * 2, nr * 2);

            if (_selection.Contains(i))
                g.DrawEllipse(selOutlinePen, pt.X - nr, pt.Y - nr, nr * 2, nr * 2);
            else
                g.DrawEllipse(outlinePen, pt.X - nr, pt.Y - nr, nr * 2, nr * 2);
        }

        // ── Draw labels ────────────────────────────────────────────────
        if (ShowLabels && _zoom > 0.5f)
        {
            using var font = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(200, 220, 220, 230));

            var labels = _graph.Labels;
            for (int i = 0; i < n; i++)
            {
                var pt = WorldToScreen(px[i], py[i]);
                float nr = r + Math.Min(degree[i] * 0.3f, 6f);
                g.DrawString(labels[i], font, labelBrush, pt.X + nr + 2, pt.Y - 6);
            }
        }

        // ── Draw selection rectangle ───────────────────────────────────
        if (_selecting)
        {
            var rect = GetSelectionRect();
            using var selPen = new Pen(Color.FromArgb(180, 100, 200, 255), 1f);
            selPen.DashStyle = DashStyle.Dash;
            using var selFill = new SolidBrush(Color.FromArgb(30, 100, 200, 255));
            g.FillRectangle(selFill, rect);
            g.DrawRectangle(selPen, rect);
        }

        // ── HUD ────────────────────────────────────────────────────────
        DrawHud(g);
    }

    /// <summary>
    /// Draw faint convex hulls or halos around groups of nodes that share
    /// the same parent at the next coarser level. Shows the coarsening
    /// structure as a ghost overlay.
    /// </summary>
    private void DrawParentHighlight(Graphics g)
    {
        if (_hierarchy == null || _graph == null) return;
        int nextLevel = _currentLevel + 1;
        if (nextLevel >= _hierarchy.LevelCount) return;

        var coarseGraph = _hierarchy.Graphs[nextLevel];
        var map = _hierarchy.FineToCoarse[_currentLevel];
        int n = _graph.NodeCount;

        // For each coarse node, collect screen positions of its children
        // and draw a translucent ellipse encompassing them
        int coarseN = coarseGraph.NodeCount;

        // Compute bounding box for each coarse node's children
        var minXs = new float[coarseN];
        var minYs = new float[coarseN];
        var maxXs = new float[coarseN];
        var maxYs = new float[coarseN];
        var counts = new int[coarseN];
        Array.Fill(minXs, float.MaxValue);
        Array.Fill(minYs, float.MaxValue);
        Array.Fill(maxXs, float.MinValue);
        Array.Fill(maxYs, float.MinValue);

        for (int i = 0; i < n; i++)
        {
            int ci = map[i];
            var pt = WorldToScreen(_graph.NodeX[i], _graph.NodeY[i]);
            if (pt.X < minXs[ci]) minXs[ci] = pt.X;
            if (pt.Y < minYs[ci]) minYs[ci] = pt.Y;
            if (pt.X > maxXs[ci]) maxXs[ci] = pt.X;
            if (pt.Y > maxYs[ci]) maxYs[ci] = pt.Y;
            counts[ci]++;
        }

        // Draw translucent ellipses for groups with >1 member
        for (int ci = 0; ci < coarseN; ci++)
        {
            if (counts[ci] <= 1) continue;

            float pad = NodeRadius + 8;
            float cx = (minXs[ci] + maxXs[ci]) / 2;
            float cy = (minYs[ci] + maxYs[ci]) / 2;
            float rx = (maxXs[ci] - minXs[ci]) / 2 + pad;
            float ry = (maxYs[ci] - minYs[ci]) / 2 + pad;

            Color c = Palette[coarseGraph.Community[ci] % Palette.Length];
            using var brush = new SolidBrush(Color.FromArgb(25, c.R, c.G, c.B));
            using var pen = new Pen(Color.FromArgb(50, c.R, c.G, c.B), 1f);
            pen.DashStyle = DashStyle.Dot;

            g.FillEllipse(brush, cx - rx, cy - ry, rx * 2, ry * 2);
            g.DrawEllipse(pen, cx - rx, cy - ry, rx * 2, ry * 2);
        }
    }

    private void DrawPlaceholder(Graphics g)
    {
        using var font = new Font("Segoe UI", 14f);
        using var brush = new SolidBrush(Color.FromArgb(100, 180, 180, 200));
        var text = "◇  No graph loaded  ◇";
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush,
            (Width - size.Width) / 2,
            (Height - size.Height) / 2);
    }

    private void DrawHud(Graphics g)
    {
        if (_graph == null) return;

        // Graph info line
        string info = $"{_graph.Title}  —  {_graph.NodeCount}n, {_graph.EdgeCount}e";
        if (_selection.Count > 0)
            info += $"  |  {_selection.Count} selected";

        // Level indicator
        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            info += $"  |  Level {_currentLevel}/{_hierarchy.LevelCount - 1}";
            if (_currentLevel == 0) info += " (finest)";
            else if (_currentLevel == _hierarchy.LevelCount - 1) info += " (coarsest)";
        }

        using var font = new Font("Cascadia Mono", 9f);
        using var brush = new SolidBrush(Color.FromArgb(160, 200, 200, 220));
        g.DrawString(info, font, brush, 8, Height - 24);

        // Level navigation hint (top-right)
        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            string hint = "PgUp/PgDn: navigate levels";
            var hintSize = g.MeasureString(hint, font);
            g.DrawString(hint, font, brush, Width - hintSize.Width - 8, 8);
        }
    }

    // ── Coordinate transforms ───────────────────────────────────────────

    /// <summary>Convert world coordinates to screen coordinates.</summary>
    public PointF WorldToScreen(double wx, double wy)
    {
        return new PointF(
            (float)(wx * _zoom + _pan.X),
            (float)(wy * _zoom + _pan.Y));
    }

    /// <summary>Convert screen coordinates to world coordinates.</summary>
    public (double wx, double wy) ScreenToWorld(float sx, float sy)
    {
        return ((sx - _pan.X) / _zoom, (sy - _pan.Y) / _zoom);
    }

    /// <summary>Convert world coordinates for node i to screen.</summary>
    public PointF WorldToScreenNode(int i)
    {
        if (_graph == null) return PointF.Empty;
        return WorldToScreen(_graph.NodeX[i], _graph.NodeY[i]);
    }

    // ── Mouse interaction ───────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus(); // ensure we get key events

        if (e.Button == MouseButtons.Left)
        {
            // Left click: start selection rectangle
            _selecting = true;
            _selStart = e.Location;
            _selEnd = e.Location;
        }
        else if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            // Middle/right click: pan
            _panning = true;
            _lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            _pan.X += e.X - _lastMouse.X;
            _pan.Y += e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            Invalidate();
        }
        else if (_selecting)
        {
            _selEnd = e.Location;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_panning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right))
        {
            _panning = false;
            Cursor = Cursors.Default;
        }
        else if (_selecting && e.Button == MouseButtons.Left)
        {
            _selecting = false;
            FinishSelection(e);
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    private void FinishSelection(MouseEventArgs e)
    {
        if (_graph == null) return;

        var rect = GetSelectionRect();

        // If it's a tiny rect (click, not drag), do single-node toggle
        if (rect.Width < 4 && rect.Height < 4)
        {
            int hit = HitTestNode(e.Location);
            bool ctrl = ModifierKeys.HasFlag(Keys.Control);
            if (!ctrl) _selection.Clear();
            if (hit >= 0)
            {
                if (_selection.Contains(hit))
                    _selection.Remove(hit);
                else
                    _selection.Add(hit);
            }
        }
        else
        {
            // Rectangle selection
            bool ctrl = ModifierKeys.HasFlag(Keys.Control);
            if (!ctrl) _selection.Clear();

            int n = _graph.NodeCount;
            for (int i = 0; i < n; i++)
            {
                var pt = WorldToScreenNode(i);
                if (rect.Contains(Point.Round(pt)))
                    _selection.Add(i);
            }
        }

        SelectionChanged?.Invoke();
    }

    private Rectangle GetSelectionRect()
    {
        int x = Math.Min(_selStart.X, _selEnd.X);
        int y = Math.Min(_selStart.Y, _selEnd.Y);
        int w = Math.Abs(_selEnd.X - _selStart.X);
        int h = Math.Abs(_selEnd.Y - _selStart.Y);
        return new Rectangle(x, y, w, h);
    }

    private int HitTestNode(Point screenPoint)
    {
        if (_graph == null) return -1;

        float bestDist = float.MaxValue;
        int bestNode = -1;

        for (int i = 0; i < _graph.NodeCount; i++)
        {
            var pt = WorldToScreenNode(i);
            float dx = screenPoint.X - pt.X;
            float dy = screenPoint.Y - pt.Y;
            float dist = dx * dx + dy * dy;

            float radius = NodeRadius + Math.Min(_graph.Degree[i] * 0.3f, 6f);
            float hitRadius = radius + 4;

            if (dist < hitRadius * hitRadius && dist < bestDist)
            {
                bestDist = dist;
                bestNode = i;
            }
        }

        return bestNode;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        float factor = e.Delta > 0 ? 1.15f : 1 / 1.15f;

        // Zoom toward mouse position
        _pan.X = e.X - (e.X - _pan.X) * factor;
        _pan.Y = e.Y - (e.Y - _pan.Y) * factor;
        _zoom *= factor;

        Invalidate();
        base.OnMouseWheel(e);
    }

    // ── Keyboard: level navigation ──────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.PageUp:
                GoCoarser();
                e.Handled = true;
                break;
            case Keys.PageDown:
                GoFiner();
                e.Handled = true;
                break;
            case Keys.Home:
                SetLevel(0);
                e.Handled = true;
                break;
            case Keys.End:
                if (_hierarchy != null)
                    SetLevel(_hierarchy.LevelCount - 1);
                e.Handled = true;
                break;
            case Keys.Escape:
                ClearSelection();
                e.Handled = true;
                break;
            case Keys.A when e.Modifiers == Keys.Control:
                SelectAll();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        // Ensure we get these keys rather than the form processing them
        return keyData switch
        {
            Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End => true,
            _ => base.IsInputKey(keyData),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAnimation();
            foreach (var b in _paletteBrushes) b.Dispose();
        }
        base.Dispose(disposing);
    }
}
