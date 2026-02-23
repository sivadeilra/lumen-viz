using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using LumenGraph;

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

    // ── Render toggles (for performance experiments) ────────────────────
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowEdges { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowNodes { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowOutlines { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowOffScreenIndicators { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSelectionGlow { get; set; } = true;

    /// <summary>LOD override: "auto" (default), "low" (force rectangles), "high" (force ellipses+outlines).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LodMode { get; set; } = "auto";

    // ── Performance instrumentation ─────────────────────────────────────
    private readonly Stopwatch _frameSw = new();
    private readonly Stopwatch _phaseSw = new();
    private int _perfFrameCount;
    private const double PerfAlpha = 0.1; // EMA smoothing factor

    // Last-frame + exponential moving average per phase
    private double _lastFrameMs, _avgFrameMs;
    private double _lastPrecomputeMs, _avgPrecomputeMs;
    private double _lastEdgesMs, _avgEdgesMs;
    private double _lastNodesMs, _avgNodesMs;
    private double _lastParentHighlightMs, _avgParentHighlightMs;
    private double _lastMinimapMs, _avgMinimapMs;
    private double _lastLevelsPanelMs, _avgLevelsPanelMs;
    private double _lastOffScreenMs, _avgOffScreenMs;
    private double _lastLabelsMs, _avgLabelsMs;
    private double _lastHudMs, _avgHudMs;
    private double _lastSelGlowMs, _avgSelGlowMs;

    private void UpdateEma(ref double avg, double sample)
    {
        if (_perfFrameCount <= 1)
            avg = sample;
        else
            avg = avg * (1 - PerfAlpha) + sample * PerfAlpha;
    }

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

        float margin = 60;
        float viewW = Width - margin * 2;
        float viewH = Height - margin - 50; // top margin + bottom space for HUD/status
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
        _frameSw.Restart();
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
        _phaseSw.Restart();
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
        double precomputeMs = _phaseSw.Elapsed.TotalMilliseconds;

        float viewL = -50f, viewT = -50f;
        float viewR = Width + 50f, viewB = Height + 50f;

        // ── Draw parent highlight ──────────────────────────────────────
        _phaseSw.Restart();
        if (ShowParentHighlight && _hierarchy != null
            && _currentLevel < _hierarchy.LevelCount - 1
            && _animTimer == null)
        {
            DrawParentHighlight(g);
        }
        double parentHighlightMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw edges (batched) ──────────────────────────────────────
        _phaseSw.Restart();
        if (ShowEdges)
        {
            int edgeAlpha = Math.Clamp((int)(EdgeAlpha * 255), 10, 255);
            using var edgePen = new Pen(Color.FromArgb(edgeAlpha, 120, 130, 150), 1f);

            var es = _graph.EdgeSource;
            var et = _graph.EdgeTarget;
            int edgeCount = _graph.EdgeCount;

            if (edgeCount > 0)
            {
                var lineBuf = new PointF[edgeCount * 2];
                int lineIdx = 0;

                for (int ei = 0; ei < edgeCount; ei++)
                {
                    float ax = _screenX[es[ei]], ay = _screenY[es[ei]];
                    float bx = _screenX[et[ei]], by = _screenY[et[ei]];

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

                if (lineIdx >= 4)
                {
                    for (int li = 0; li < lineIdx; li += 2)
                        g.DrawLine(edgePen, lineBuf[li], lineBuf[li + 1]);
                }
                else if (lineIdx == 2)
                {
                    g.DrawLine(edgePen, lineBuf[0], lineBuf[1]);
                }
            }
        }
        double edgesMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw selected node glow (only on-screen nodes) ────────────
        _phaseSw.Restart();
        if (ShowSelectionGlow && _selection.Count > 0)
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
        double selGlowMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw nodes (LOD-aware, off-screen culling) ────────────────
        _phaseSw.Restart();
        float r = NodeRadius;
        using var outlinePen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f);
        using var selOutlinePen = new Pen(Color.FromArgb(255, 255, 255, 100), 2f);

        // LOD determination with override
        bool lowDetail;
        if (LodMode == "low") lowDetail = true;
        else if (LodMode == "high") lowDetail = false;
        else lowDetail = (n > 2000 && _zoom < 1.5f); // auto

        if (lowDetail)
            g.SmoothingMode = SmoothingMode.None;

        int offScreenCount = 0;
        if (ShowNodes)
        {
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
                    g.FillRectangle(brush, sx - nr, sy - nr, nr * 2, nr * 2);
                }
                else
                {
                    g.FillEllipse(brush, sx - nr, sy - nr, nr * 2, nr * 2);
                    if (ShowOutlines)
                    {
                        if (_selection.Contains(i))
                            g.DrawEllipse(selOutlinePen, sx - nr, sy - nr, nr * 2, nr * 2);
                        else
                            g.DrawEllipse(outlinePen, sx - nr, sy - nr, nr * 2, nr * 2);
                    }
                }
            }
        }
        double nodesMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw off-screen indicators at viewport edges ──────────────
        _phaseSw.Restart();
        if (ShowOffScreenIndicators && offScreenCount > 0 && offScreenCount < 2000)
            DrawOffScreenIndicators(g);
        double offScreenMs = _phaseSw.Elapsed.TotalMilliseconds;

        // Restore anti-aliasing for overlays
        if (lowDetail && AntiAlias)
            g.SmoothingMode = SmoothingMode.AntiAlias;

        // ── Draw labels ────────────────────────────────────────────────
        _phaseSw.Restart();
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
        double labelsMs = _phaseSw.Elapsed.TotalMilliseconds;

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
        _phaseSw.Restart();
        if (ShowMinimap)
            DrawMinimap(g);
        double minimapMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw coarsening levels panel (top-left) ────────────────────
        _phaseSw.Restart();
        if (ShowLevelsPanel && _hierarchy != null && _hierarchy.LevelCount > 1)
            DrawLevelsPanel(g);
        double levelsPanelMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── HUD ────────────────────────────────────────────────────────
        _phaseSw.Restart();
        DrawHud(g);
        double hudMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Record performance stats ───────────────────────────────────
        double frameMs = _frameSw.Elapsed.TotalMilliseconds;
        _perfFrameCount++;
        _lastFrameMs = frameMs;
        _lastPrecomputeMs = precomputeMs;
        _lastEdgesMs = edgesMs;
        _lastNodesMs = nodesMs;
        _lastParentHighlightMs = parentHighlightMs;
        _lastMinimapMs = minimapMs;
        _lastLevelsPanelMs = levelsPanelMs;
        _lastOffScreenMs = offScreenMs;
        _lastLabelsMs = labelsMs;
        _lastHudMs = hudMs;
        _lastSelGlowMs = selGlowMs;
        UpdateEma(ref _avgFrameMs, frameMs);
        UpdateEma(ref _avgPrecomputeMs, precomputeMs);
        UpdateEma(ref _avgEdgesMs, edgesMs);
        UpdateEma(ref _avgNodesMs, nodesMs);
        UpdateEma(ref _avgParentHighlightMs, parentHighlightMs);
        UpdateEma(ref _avgMinimapMs, minimapMs);
        UpdateEma(ref _avgLevelsPanelMs, levelsPanelMs);
        UpdateEma(ref _avgOffScreenMs, offScreenMs);
        UpdateEma(ref _avgLabelsMs, labelsMs);
        UpdateEma(ref _avgHudMs, hudMs);
        UpdateEma(ref _avgSelGlowMs, selGlowMs);
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
        int mmY = Height - MinimapSize - MinimapMargin - 32; // above HUD + status bar
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
        int py = 32; // below menu strip area with margin

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

    /// <summary>Margin constants for overlay text.</summary>
    private const int HudMarginX = 10;
    private const int HudMarginBottom = 6;
    private const int HintMarginTop = 30;  // below menu strip
    private const int HintMarginX = 10;

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

        // Frame time (show average after warm-up)
        if (_perfFrameCount > 3)
            info += $"  |  {_avgFrameMs:F1}ms";

        using var font = new Font("Cascadia Mono", 9f);
        using var brush = new SolidBrush(Color.FromArgb(160, 200, 200, 220));

        // Draw HUD at bottom with proper margin above status bar
        var infoSize = g.MeasureString(info, font);
        g.DrawString(info, font, brush, HudMarginX, Height - infoSize.Height - HudMarginBottom);

        // Hint text at top-right, below menu strip
        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            string hint = "PgUp/PgDn: levels  |  Home: fit view  |  +/−: zoom  |  Arrows: pan";
            var hintSize = g.MeasureString(hint, font);
            g.DrawString(hint, font, brush, Width - hintSize.Width - HintMarginX, HintMarginTop);
        }
    }

    // ── Performance stats query ─────────────────────────────────────────

    /// <summary>
    /// Returns a dictionary of performance stats for MCP querying.
    /// All times in milliseconds.
    /// </summary>
    public Dictionary<string, object> GetRenderStats()
    {
        return new Dictionary<string, object>
        {
            ["frame_count"] = _perfFrameCount,
            ["last_frame_ms"] = Math.Round(_lastFrameMs, 3),
            ["avg_frame_ms"] = Math.Round(_avgFrameMs, 3),
            ["phases"] = new Dictionary<string, object>
            {
                ["precompute"] = new { last = Math.Round(_lastPrecomputeMs, 3), avg = Math.Round(_avgPrecomputeMs, 3) },
                ["parent_highlight"] = new { last = Math.Round(_lastParentHighlightMs, 3), avg = Math.Round(_avgParentHighlightMs, 3) },
                ["edges"] = new { last = Math.Round(_lastEdgesMs, 3), avg = Math.Round(_avgEdgesMs, 3) },
                ["selection_glow"] = new { last = Math.Round(_lastSelGlowMs, 3), avg = Math.Round(_avgSelGlowMs, 3) },
                ["nodes"] = new { last = Math.Round(_lastNodesMs, 3), avg = Math.Round(_avgNodesMs, 3) },
                ["off_screen"] = new { last = Math.Round(_lastOffScreenMs, 3), avg = Math.Round(_avgOffScreenMs, 3) },
                ["labels"] = new { last = Math.Round(_lastLabelsMs, 3), avg = Math.Round(_avgLabelsMs, 3) },
                ["minimap"] = new { last = Math.Round(_lastMinimapMs, 3), avg = Math.Round(_avgMinimapMs, 3) },
                ["levels_panel"] = new { last = Math.Round(_lastLevelsPanelMs, 3), avg = Math.Round(_avgLevelsPanelMs, 3) },
                ["hud"] = new { last = Math.Round(_lastHudMs, 3), avg = Math.Round(_avgHudMs, 3) },
            },
            ["settings"] = new Dictionary<string, object>
            {
                ["anti_alias"] = AntiAlias,
                ["show_edges"] = ShowEdges,
                ["show_nodes"] = ShowNodes,
                ["show_outlines"] = ShowOutlines,
                ["show_labels"] = ShowLabels,
                ["show_minimap"] = ShowMinimap,
                ["show_levels_panel"] = ShowLevelsPanel,
                ["show_parent_highlight"] = ShowParentHighlight,
                ["show_off_screen_indicators"] = ShowOffScreenIndicators,
                ["show_selection_glow"] = ShowSelectionGlow,
                ["lod_mode"] = LodMode,
                ["node_radius"] = NodeRadius,
                ["edge_alpha"] = EdgeAlpha,
            },
        };
    }

    /// <summary>Resets the EMA counters so the next measurements start fresh.</summary>
    public void ResetRenderStats()
    {
        _perfFrameCount = 0;
        _avgFrameMs = _avgPrecomputeMs = _avgEdgesMs = _avgNodesMs = 0;
        _avgParentHighlightMs = _avgMinimapMs = _avgLevelsPanelMs = 0;
        _avgOffScreenMs = _avgLabelsMs = _avgHudMs = _avgSelGlowMs = 0;
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

    private const float KeyboardZoomFactor = 1.25f;
    private const float KeyboardPanStep = 60f;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            // ── Level navigation ────────────────────────────────────
            case Keys.PageUp:
                GoCoarser();
                e.Handled = true;
                break;
            case Keys.PageDown:
                GoFiner();
                e.Handled = true;
                break;
            case Keys.D0 when e.Modifiers == Keys.None:  // 0 = finest
                SetLevel(0);
                e.Handled = true;
                break;
            case Keys.D9 when e.Modifiers == Keys.None:  // 9 = coarsest
                if (_hierarchy != null)
                    SetLevel(_hierarchy.LevelCount - 1);
                e.Handled = true;
                break;

            // ── View navigation ─────────────────────────────────────
            case Keys.Home:     // re-center / fit view
            case Keys.F:        // also F for "fit"
                AutoFit();
                Invalidate();
                e.Handled = true;
                break;
            case Keys.End:      // jump to coarsest level
                if (_hierarchy != null)
                    SetLevel(_hierarchy.LevelCount - 1);
                e.Handled = true;
                break;

            // ── Zoom ────────────────────────────────────────────────
            case Keys.Oemplus or Keys.Add:   // + or = key
                ZoomCenter(KeyboardZoomFactor);
                e.Handled = true;
                break;
            case Keys.OemMinus or Keys.Subtract:  // - key
                ZoomCenter(1f / KeyboardZoomFactor);
                e.Handled = true;
                break;

            // ── Pan with arrow keys ─────────────────────────────────
            case Keys.Left:
                _pan.X += KeyboardPanStep;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.Right:
                _pan.X -= KeyboardPanStep;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.Up:
                _pan.Y += KeyboardPanStep;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.Down:
                _pan.Y -= KeyboardPanStep;
                Invalidate();
                e.Handled = true;
                break;

            // ── Selection ───────────────────────────────────────────
            case Keys.Escape:
                ClearSelection();
                e.Handled = true;
                break;
            case Keys.A when e.Modifiers == Keys.Control:
                SelectAll();
                e.Handled = true;
                break;

            // ── Toggles ─────────────────────────────────────────────
            case Keys.L when e.Modifiers == Keys.None:   // L = labels
                ShowLabels = !ShowLabels;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.M when e.Modifiers == Keys.None:   // M = minimap
                ShowMinimap = !ShowMinimap;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.E when e.Modifiers == Keys.None:   // E = edges
                ShowEdges = !ShowEdges;
                Invalidate();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Zoom centered on the control's midpoint.</summary>
    private void ZoomCenter(float factor)
    {
        float cx = Width / 2f, cy = Height / 2f;
        _pan.X = cx - (cx - _pan.X) * factor;
        _pan.Y = cy - (cy - _pan.Y) * factor;
        _zoom *= factor;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) switch
        {
            Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
                or Keys.Left or Keys.Right or Keys.Up or Keys.Down => true,
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
