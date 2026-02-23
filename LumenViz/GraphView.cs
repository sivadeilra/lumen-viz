using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LumenViz;

/// <summary>
/// Custom WinForms control that renders a graph with force-directed layout.
/// Supports zoom, pan, community coloring, node labels, rubber-band
/// node selection, coarsening level navigation with animated transitions,
/// parent-child relationship highlighting, a minimap, off-screen node
/// culling with edge indicators, and a clickable coarsening levels panel.
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

    // ── Coarsening levels panel (top-left overlay) ──────────────────────
    private const int LevelsPanelWidth = 260;
    private const int LevelRowHeight = 20;
    private const int LevelsPanelPadTop = 28;   // below menu strip
    private const int LevelsPanelPadLeft = 6;
    private int _hoveredLevelRow = -1;           // for hover highlight

    // ── Animation ───────────────────────────────────────────────────────
    private System.Windows.Forms.Timer? _animTimer;
    private double[]? _animFromX, _animFromY;
    private double[]? _animToX, _animToY;
    private int[]? _animMapping;
    private int _animFrame;
    private const int AnimFrames = 20;
    private const int AnimIntervalMs = 16; // ~60 fps
    private bool _animatingUp;
    private int _animTargetLevel;

    // ── Viewport animation (smooth pan/zoom after level transition) ─────
    private System.Windows.Forms.Timer? _vpAnimTimer;
    private float _vpFromPanX, _vpFromPanY, _vpFromZoom;
    private float _vpToPanX, _vpToPanY, _vpToZoom;
    private int _vpAnimFrame;
    private const int VpAnimFrames = 30;  // ~500ms at 16ms interval
    private const int VpAnimIntervalMs = 16;

    // ── Minimap ─────────────────────────────────────────────────────────
    private const int MinimapSize = 160;
    private const int MinimapMargin = 8;

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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowParentHighlight { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowMinimap { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLevelsPanel { get; set; } = true;

    private static readonly Color[] Palette = new[]
    {
        Color.FromArgb(102, 194, 255),
        Color.FromArgb(255, 128, 102),
        Color.FromArgb(128, 230, 128),
        Color.FromArgb(255, 204, 77),
        Color.FromArgb(204, 153, 255),
        Color.FromArgb(255, 153, 204),
        Color.FromArgb(102, 230, 204),
        Color.FromArgb(255, 179, 102),
        Color.FromArgb(153, 204, 255),
        Color.FromArgb(230, 230, 128),
        Color.FromArgb(204, 128, 255),
        Color.FromArgb(128, 204, 179),
    };

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

    public bool GoCoarser()
    {
        if (_hierarchy == null || _currentLevel >= _hierarchy.LevelCount - 1)
            return false;
        AnimateToLevel(_currentLevel + 1);
        return true;
    }

    public bool GoFiner()
    {
        if (_hierarchy == null || _currentLevel <= 0)
            return false;
        AnimateToLevel(_currentLevel - 1);
        return true;
    }

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
        StopVpAnimation();

        var fromGraph = _hierarchy.Graphs[_currentLevel];
        var toGraph = _hierarchy.Graphs[targetLevel];
        bool goingUp = targetLevel > _currentLevel;

        _animatingUp = goingUp;
        _animTargetLevel = targetLevel;

        if (goingUp)
        {
            int fromN = fromGraph.NodeCount;
            _animFromX = new double[fromN];
            _animFromY = new double[fromN];
            _animToX = new double[fromN];
            _animToY = new double[fromN];
            _animMapping = new int[fromN];

            Array.Copy(fromGraph.NodeX, _animFromX, fromN);
            Array.Copy(fromGraph.NodeY, _animFromY, fromN);

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
            int toN = toGraph.NodeCount;
            _animFromX = new double[toN];
            _animFromY = new double[toN];
            _animToX = new double[toN];
            _animToY = new double[toN];
            _animMapping = null;

            for (int i = 0; i < toN; i++)
            {
                int coarseIdx = _hierarchy.MapUp(i, targetLevel, _currentLevel);
                _animFromX[i] = fromGraph.NodeX[coarseIdx];
                _animFromY[i] = fromGraph.NodeY[coarseIdx];
                _animToX[i] = toGraph.NodeX[i];
                _animToY[i] = toGraph.NodeY[i];
            }

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

        double t = (double)_animFrame / AnimFrames;
        t = t * t * (3.0 - 2.0 * t); // smoothstep

        if (_animatingUp)
        {
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

        _currentLevel = _animTargetLevel;
        _graph = _hierarchy!.Graphs[_currentLevel];

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

        // Instead of snapping to new bounds, smoothly animate the viewport.
        // Save current pan/zoom, compute target, then animate between them.
        _vpFromPanX = _pan.X;
        _vpFromPanY = _pan.Y;
        _vpFromZoom = _zoom;

        AutoFit(); // computes new _pan and _zoom

        _vpToPanX = _pan.X;
        _vpToPanY = _pan.Y;
        _vpToZoom = _zoom;

        // Restore old viewport — the timer will interpolate to the new one
        _pan = new PointF(_vpFromPanX, _vpFromPanY);
        _zoom = _vpFromZoom;

        _vpAnimFrame = 0;
        _vpAnimTimer = new System.Windows.Forms.Timer { Interval = VpAnimIntervalMs };
        _vpAnimTimer.Tick += OnVpAnimTick;
        _vpAnimTimer.Start();

        Invalidate();
        LevelChanged?.Invoke();
    }

    private void OnVpAnimTick(object? sender, EventArgs e)
    {
        _vpAnimFrame++;
        if (_vpAnimFrame >= VpAnimFrames)
        {
            StopVpAnimation();
            _pan = new PointF(_vpToPanX, _vpToPanY);
            _zoom = _vpToZoom;
            Invalidate();
            return;
        }

        double t = (double)_vpAnimFrame / VpAnimFrames;
        t = t * t * (3.0 - 2.0 * t); // smoothstep

        _pan = new PointF(
            (float)(_vpFromPanX + (_vpToPanX - _vpFromPanX) * t),
            (float)(_vpFromPanY + (_vpToPanY - _vpFromPanY) * t));
        _zoom = (float)(_vpFromZoom + (_vpToZoom - _vpFromZoom) * t);

        Invalidate();
    }

    private void StopVpAnimation()
    {
        if (_vpAnimTimer != null)
        {
            _vpAnimTimer.Stop();
            _vpAnimTimer.Tick -= OnVpAnimTick;
            _vpAnimTimer.Dispose();
            _vpAnimTimer = null;
        }
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

    // Reusable screen-coordinate buffers (avoid per-frame allocation)
    private float[] _screenX = Array.Empty<float>();
    private float[] _screenY = Array.Empty<float>();

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
        var community = _graph.Community;

        // ── Pre-compute all screen coordinates once ────────────────────
        if (_screenX.Length < n)
        {
            _screenX = new float[n];
            _screenY = new float[n];
        }
        for (int i = 0; i < n; i++)
        {
            _screenX[i] = (float)(px[i] * _zoom + _pan.X);
            _screenY[i] = (float)(py[i] * _zoom + _pan.Y);
        }

        float viewL = -50f, viewT = -50f;
        float viewR = Width + 50f, viewB = Height + 50f;

        // ── Draw parent highlight ──────────────────────────────────────
        if (ShowParentHighlight && _hierarchy != null
            && _currentLevel < _hierarchy.LevelCount - 1
            && _animTimer == null)
        {
            DrawParentHighlight(g);
        }

        // ── Draw edges (batched) ──────────────────────────────────────
        int edgeAlpha = Math.Clamp((int)(EdgeAlpha * 255), 10, 255);
        using var edgePen = new Pen(Color.FromArgb(edgeAlpha, 120, 130, 150), 1f);

        var es = _graph.EdgeSource;
        var et = _graph.EdgeTarget;
        int edgeCount = _graph.EdgeCount;

        // Build a line-segment buffer: pairs of points for DrawLines
        // We use a PointF[] sized for worst case, then trim
        if (edgeCount > 0)
        {
            var lineBuf = new PointF[edgeCount * 2];
            int lineIdx = 0;

            for (int ei = 0; ei < edgeCount; ei++)
            {
                float ax = _screenX[es[ei]], ay = _screenY[es[ei]];
                float bx = _screenX[et[ei]], by = _screenY[et[ei]];

                // Cull edges entirely outside the viewport
                bool aIn = ax >= viewL && ax <= viewR && ay >= viewT && ay <= viewB;
                bool bIn = bx >= viewL && bx <= viewR && by >= viewT && by <= viewB;
                if (!aIn && !bIn)
                {
                    if (!LineIntersectsRect(
                        new PointF(ax, ay), new PointF(bx, by),
                        new RectangleF(viewL, viewT, viewR - viewL, viewB - viewT)))
                        continue;
                }

                lineBuf[lineIdx++] = new PointF(ax, ay);
                lineBuf[lineIdx++] = new PointF(bx, by);
            }

            // Draw all visible edges in one batched call
            if (lineIdx >= 4)
            {
                // DrawLines connects consecutive points, but we want disconnected
                // line segments. Use a loop drawing pairs to avoid false connections.
                // For n < ~20k segments, the overhead is tolerable.
                for (int li = 0; li < lineIdx; li += 2)
                    g.DrawLine(edgePen, lineBuf[li], lineBuf[li + 1]);
            }
            else if (lineIdx == 2)
            {
                g.DrawLine(edgePen, lineBuf[0], lineBuf[1]);
            }
        }

        // ── Draw selected node glow (only on-screen nodes) ────────────
        if (_selection.Count > 0)
        {
            using var glowBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 100));
            float glowR = NodeRadius + 10;
            foreach (int i in _selection)
            {
                if (i >= n) continue;
                float sx = _screenX[i], sy = _screenY[i];
                if (sx < viewL || sx > viewR || sy < viewT || sy > viewB) continue;
                g.FillEllipse(glowBrush, sx - glowR, sy - glowR, glowR * 2, glowR * 2);
            }
        }

        // ── Draw nodes (LOD-aware, off-screen culling) ────────────────
        float r = NodeRadius;
        using var outlinePen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f);
        using var selOutlinePen = new Pen(Color.FromArgb(255, 255, 255, 100), 2f);

        // LOD: when there are many on-screen nodes, use simpler rendering
        bool lowDetail = (n > 2000 && _zoom < 1.5f);

        // In low-detail mode, disable anti-aliasing for speed
        if (lowDetail)
            g.SmoothingMode = SmoothingMode.None;

        int offScreenCount = 0;
        for (int i = 0; i < n; i++)
        {
            float sx = _screenX[i], sy = _screenY[i];
            if (sx < viewL || sx > viewR || sy < viewT || sy > viewB)
            {
                offScreenCount++;
                continue;
            }

            float nr = r + Math.Min(degree[i] * 0.3f, 6f);
            var brush = _paletteBrushes[community[i] % Palette.Length];

            if (lowDetail)
            {
                // Fast path: filled rectangles, no outlines
                g.FillRectangle(brush, sx - nr, sy - nr, nr * 2, nr * 2);
            }
            else
            {
                g.FillEllipse(brush, sx - nr, sy - nr, nr * 2, nr * 2);
                if (_selection.Contains(i))
                    g.DrawEllipse(selOutlinePen, sx - nr, sy - nr, nr * 2, nr * 2);
                else
                    g.DrawEllipse(outlinePen, sx - nr, sy - nr, nr * 2, nr * 2);
            }
        }

        // ── Draw off-screen indicators at viewport edges ──────────────
        // Skip if too many off-screen (drawing thousands of dots is slow)
        if (offScreenCount > 0 && offScreenCount < 2000)
            DrawOffScreenIndicators(g);

        // Restore anti-aliasing for overlays
        if (lowDetail && AntiAlias)
            g.SmoothingMode = SmoothingMode.AntiAlias;

        // ── Draw labels ────────────────────────────────────────────────
        if (ShowLabels && _zoom > 0.5f)
        {
            using var font = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(200, 220, 220, 230));

            var labels = _graph.Labels;
            for (int i = 0; i < n; i++)
            {
                float sx = _screenX[i], sy = _screenY[i];
                if (sx < viewL || sx > viewR || sy < viewT || sy > viewB) continue;

                float nr = r + Math.Min(degree[i] * 0.3f, 6f);
                g.DrawString(labels[i], font, labelBrush, sx + nr + 2, sy - 6);
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

        // ── Draw minimap (bottom-right) ────────────────────────────────
        if (ShowMinimap)
            DrawMinimap(g);

        // ── Draw coarsening levels panel (top-left) ────────────────────
        if (ShowLevelsPanel && _hierarchy != null && _hierarchy.LevelCount > 1)
            DrawLevelsPanel(g);

        // ── HUD ────────────────────────────────────────────────────────
        DrawHud(g);
    }

    // ── Off-screen indicators ───────────────────────────────────────────

    /// <summary>
    /// Draw small dots along the viewport edges to indicate
    /// where off-screen nodes lie. Uses pre-computed screen coords.
    /// </summary>
    private void DrawOffScreenIndicators(Graphics g)
    {
        if (_graph == null) return;

        int n = _graph.NodeCount;
        using var indicatorBrush = new SolidBrush(Color.FromArgb(120, 255, 200, 80));
        float dotR = 3f;
        float w = Width, h = Height;

        for (int i = 0; i < n; i++)
        {
            float sx = _screenX[i], sy = _screenY[i];
            if (sx >= -50 && sx <= w + 50 && sy >= -50 && sy <= h + 50)
                continue;

            // Clamp to viewport edge
            float cx = Math.Clamp(sx, 2, w - 2);
            float cy = Math.Clamp(sy, 2, h - 2);
            g.FillEllipse(indicatorBrush, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
        }
    }

    // ── Minimap ─────────────────────────────────────────────────────────

    /// <summary>
    /// Draw a small overview of the entire graph in the bottom-right
    /// corner, with a rectangle showing the current viewport.
    /// </summary>
    private void DrawMinimap(Graphics g)
    {
        if (_graph == null || _graph.NodeCount == 0) return;

        int n = _graph.NodeCount;
        var px = _graph.NodeX;
        var py = _graph.NodeY;

        // Compute world bounds
        double wMinX = double.MaxValue, wMinY = double.MaxValue;
        double wMaxX = double.MinValue, wMaxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (px[i] < wMinX) wMinX = px[i];
            if (py[i] < wMinY) wMinY = py[i];
            if (px[i] > wMaxX) wMaxX = px[i];
            if (py[i] > wMaxY) wMaxY = py[i];
        }

        double wW = wMaxX - wMinX;
        double wH = wMaxY - wMinY;
        if (wW < 1) wW = 1;
        if (wH < 1) wH = 1;

        // Minimap position (bottom-right)
        int mmX = Width - MinimapSize - MinimapMargin;
        int mmY = Height - MinimapSize - MinimapMargin - 24; // above HUD
        int mmW = MinimapSize;
        int mmH = MinimapSize;

        // Fit graph into minimap preserving aspect ratio
        double scale = Math.Min((double)(mmW - 8) / wW, (double)(mmH - 8) / wH);
        double offX = mmX + 4 + ((mmW - 8) - wW * scale) / 2;
        double offY = mmY + 4 + ((mmH - 8) - wH * scale) / 2;

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(180, 16, 16, 24));
        using var borderPen = new Pen(Color.FromArgb(100, 100, 110, 140), 1f);
        g.FillRectangle(bgBrush, mmX, mmY, mmW, mmH);
        g.DrawRectangle(borderPen, mmX, mmY, mmW, mmH);

        // Draw nodes as tiny dots
        using var nodeBrush = new SolidBrush(Color.FromArgb(160, 140, 160, 200));
        for (int i = 0; i < n; i++)
        {
            float mx = (float)((px[i] - wMinX) * scale + offX);
            float my = (float)((py[i] - wMinY) * scale + offY);
            g.FillRectangle(nodeBrush, mx, my, 1.5f, 1.5f);
        }

        // Draw viewport rectangle
        // The current viewport in world coords:
        var (vwTL_x, vwTL_y) = ScreenToWorld(0, 0);
        var (vwBR_x, vwBR_y) = ScreenToWorld(Width, Height);

        float vx1 = (float)((vwTL_x - wMinX) * scale + offX);
        float vy1 = (float)((vwTL_y - wMinY) * scale + offY);
        float vx2 = (float)((vwBR_x - wMinX) * scale + offX);
        float vy2 = (float)((vwBR_y - wMinY) * scale + offY);

        // Clamp to minimap bounds
        vx1 = Math.Max(vx1, mmX);
        vy1 = Math.Max(vy1, mmY);
        vx2 = Math.Min(vx2, mmX + mmW);
        vy2 = Math.Min(vy2, mmY + mmH);

        if (vx2 > vx1 && vy2 > vy1)
        {
            using var vpPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1f);
            using var vpFill = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
            g.FillRectangle(vpFill, vx1, vy1, vx2 - vx1, vy2 - vy1);
            g.DrawRectangle(vpPen, vx1, vy1, vx2 - vx1, vy2 - vy1);
        }

        // Label
        using var mmFont = new Font("Cascadia Mono", 7f);
        using var mmLabelBrush = new SolidBrush(Color.FromArgb(120, 160, 160, 180));
        g.DrawString("minimap", mmFont, mmLabelBrush, mmX + 3, mmY + 1);
    }

    // ── Coarsening levels panel ─────────────────────────────────────────

    /// <summary>
    /// Draw a clickable panel showing all coarsening levels with
    /// node count, edge count, and estimated memory usage.
    /// </summary>
    private void DrawLevelsPanel(Graphics g)
    {
        if (_hierarchy == null) return;

        int levelCount = _hierarchy.LevelCount;
        int panelH = LevelsPanelPadTop + levelCount * LevelRowHeight + 8;
        int panelW = LevelsPanelWidth;
        int px = LevelsPanelPadLeft;
        int py = 28; // below menu strip area

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(200, 16, 16, 24));
        using var borderPen = new Pen(Color.FromArgb(80, 100, 110, 140), 1f);
        g.FillRectangle(bgBrush, px, py, panelW, panelH);
        g.DrawRectangle(borderPen, px, py, panelW, panelH);

        // Header
        using var headerFont = new Font("Cascadia Mono", 8.5f, FontStyle.Bold);
        using var headerBrush = new SolidBrush(Color.FromArgb(200, 200, 210, 230));
        g.DrawString("Coarsening Levels", headerFont, headerBrush, px + 6, py + 4);

        // Column headers
        using var colFont = new Font("Cascadia Mono", 7f);
        using var colBrush = new SolidBrush(Color.FromArgb(120, 160, 160, 180));
        int headerY = py + 16;
        g.DrawString("Lvl   Nodes   Edges    Memory", colFont, colBrush, px + 6, headerY);

        // Rows
        using var rowFont = new Font("Cascadia Mono", 8f);
        using var normalBrush = new SolidBrush(Color.FromArgb(180, 180, 190, 210));
        using var currentBrush = new SolidBrush(Color.FromArgb(255, 102, 194, 255));
        using var hoverBg = new SolidBrush(Color.FromArgb(40, 100, 200, 255));
        using var currentBg = new SolidBrush(Color.FromArgb(30, 102, 194, 255));

        for (int i = 0; i < levelCount; i++)
        {
            int rowY = py + LevelsPanelPadTop + i * LevelRowHeight;
            bool isCurrent = (i == _currentLevel);
            bool isHovered = (i == _hoveredLevelRow);

            // Row background
            if (isCurrent)
                g.FillRectangle(currentBg, px + 1, rowY, panelW - 2, LevelRowHeight);
            else if (isHovered)
                g.FillRectangle(hoverBg, px + 1, rowY, panelW - 2, LevelRowHeight);

            var graph = _hierarchy.Graphs[i];
            long memBytes = graph.EstimateMemoryBytes();
            string memStr = FormatBytes(memBytes);

            string line = $" {i,2}  {graph.NodeCount,6}  {graph.EdgeCount,6}  {memStr,8}";
            g.DrawString(line, rowFont, isCurrent ? currentBrush : normalBrush,
                px + 4, rowY + 2);

            // Current level indicator
            if (isCurrent)
                g.DrawString("▶", rowFont, currentBrush, px + panelW - 20, rowY + 2);
        }
    }

    /// <summary>Get the levels panel bounding rect for hit-testing.</summary>
    private Rectangle GetLevelsPanelRect()
    {
        if (_hierarchy == null) return Rectangle.Empty;
        int levelCount = _hierarchy.LevelCount;
        int panelH = LevelsPanelPadTop + levelCount * LevelRowHeight + 8;
        return new Rectangle(LevelsPanelPadLeft, 28, LevelsPanelWidth, panelH);
    }

    /// <summary>Hit-test which level row a mouse point is over. Returns -1 if none.</summary>
    private int HitTestLevelRow(Point pt)
    {
        if (_hierarchy == null || !ShowLevelsPanel) return -1;
        var panelRect = GetLevelsPanelRect();
        if (!panelRect.Contains(pt)) return -1;

        int rowIdx = (pt.Y - panelRect.Y - LevelsPanelPadTop) / LevelRowHeight;
        if (rowIdx < 0 || rowIdx >= _hierarchy.LevelCount) return -1;
        return rowIdx;
    }

    // ── Parent highlight ────────────────────────────────────────────────

    private void DrawParentHighlight(Graphics g)
    {
        if (_hierarchy == null || _graph == null) return;
        int nextLevel = _currentLevel + 1;
        if (nextLevel >= _hierarchy.LevelCount) return;

        var coarseGraph = _hierarchy.Graphs[nextLevel];
        var map = _hierarchy.FineToCoarse[_currentLevel];
        int n = _graph.NodeCount;
        int coarseN = coarseGraph.NodeCount;

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
            float sx = _screenX[i], sy = _screenY[i];
            if (sx < minXs[ci]) minXs[ci] = sx;
            if (sy < minYs[ci]) minYs[ci] = sy;
            if (sx > maxXs[ci]) maxXs[ci] = sx;
            if (sy > maxYs[ci]) maxYs[ci] = sy;
            counts[ci]++;
        }

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

        string info = $"{_graph.Title}  —  {_graph.NodeCount}n, {_graph.EdgeCount}e";
        if (_selection.Count > 0)
            info += $"  |  {_selection.Count} selected";

        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            info += $"  |  Level {_currentLevel}/{_hierarchy.LevelCount - 1}";
            if (_currentLevel == 0) info += " (finest)";
            else if (_currentLevel == _hierarchy.LevelCount - 1) info += " (coarsest)";
        }

        using var font = new Font("Cascadia Mono", 9f);
        using var brush = new SolidBrush(Color.FromArgb(160, 200, 200, 220));
        g.DrawString(info, font, brush, 8, Height - 24);

        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            string hint = "PgUp/PgDn: navigate levels  |  Click level panel to jump";
            var hintSize = g.MeasureString(hint, font);
            g.DrawString(hint, font, brush, Width - hintSize.Width - 8, 8);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Fast line-rect intersection test.</summary>
    private static bool LineIntersectsRect(PointF a, PointF b, RectangleF rect)
    {
        // Cohen-Sutherland outcode approach (simplified)
        float xmin = rect.Left, xmax = rect.Right;
        float ymin = rect.Top, ymax = rect.Bottom;

        float dx = b.X - a.X;
        float dy = b.Y - a.Y;

        // Check horizontal and vertical slab intersections
        float tmin = 0, tmax = 1;

        if (Math.Abs(dx) > 0.001f)
        {
            float t1 = (xmin - a.X) / dx;
            float t2 = (xmax - a.X) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        else if (a.X < xmin || a.X > xmax) return false;

        if (Math.Abs(dy) > 0.001f)
        {
            float t1 = (ymin - a.Y) / dy;
            float t2 = (ymax - a.Y) / dy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        else if (a.Y < ymin || a.Y > ymax) return false;

        return true;
    }

    /// <summary>Format a byte count as human-readable (KB, MB, etc.).</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ── Coordinate transforms ───────────────────────────────────────────

    public PointF WorldToScreen(double wx, double wy)
    {
        return new PointF(
            (float)(wx * _zoom + _pan.X),
            (float)(wy * _zoom + _pan.Y));
    }

    public (double wx, double wy) ScreenToWorld(float sx, float sy)
    {
        return ((sx - _pan.X) / _zoom, (sy - _pan.Y) / _zoom);
    }

    public PointF WorldToScreenNode(int i)
    {
        if (_graph == null) return PointF.Empty;
        return WorldToScreen(_graph.NodeX[i], _graph.NodeY[i]);
    }

    // ── Mouse interaction ───────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        // Check if clicking on the levels panel
        if (e.Button == MouseButtons.Left && ShowLevelsPanel
            && _hierarchy != null && _hierarchy.LevelCount > 1)
        {
            int hitLevel = HitTestLevelRow(e.Location);
            if (hitLevel >= 0 && hitLevel != _currentLevel)
            {
                SetLevel(hitLevel);
                return; // consume the click
            }
        }

        if (e.Button == MouseButtons.Left)
        {
            _selecting = true;
            _selStart = e.Location;
            _selEnd = e.Location;
        }
        else if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
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
        else
        {
            // Hover tracking for levels panel
            int oldHover = _hoveredLevelRow;
            _hoveredLevelRow = HitTestLevelRow(e.Location);
            if (_hoveredLevelRow != oldHover)
            {
                Cursor = _hoveredLevelRow >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
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

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoveredLevelRow >= 0)
        {
            _hoveredLevelRow = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    private void FinishSelection(MouseEventArgs e)
    {
        if (_graph == null) return;

        var rect = GetSelectionRect();

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

        _pan.X = e.X - (e.X - _pan.X) * factor;
        _pan.Y = e.Y - (e.Y - _pan.Y) * factor;
        _zoom *= factor;

        Invalidate();
        base.OnMouseWheel(e);
    }

    // ── Keyboard ────────────────────────────────────────────────────────

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
            StopVpAnimation();
            foreach (var b in _paletteBrushes) b.Dispose();
        }
        base.Dispose(disposing);
    }
}
