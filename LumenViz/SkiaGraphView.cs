using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SkiaSharp;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// SkiaSharp-based graph renderer — same visual output as GraphView but
/// using Skia's SIMD-optimized software rasterizer instead of GDI+.
/// Created for A/B performance comparison.
///
/// Renders to an SKBitmap, then blits to the WinForms control surface.
/// The blit overhead is tracked separately from Skia render time.
/// </summary>
public class SkiaGraphView : Control
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
    public IReadOnlyCollection<int> Selection => _selection;
    public event Action? SelectionChanged;

    // ── Coarsening hierarchy ────────────────────────────────────────────
    private CoarseningHierarchy? _hierarchy;
    private int _currentLevel;
    public int CurrentLevel => _currentLevel;
    public int LevelCount => _hierarchy?.LevelCount ?? 1;
    public event Action? LevelChanged;

    // ── Coarsening levels panel (top-left overlay) ──────────────────────
    private const int LevelsPanelWidth = 260;
    private const int LevelRowHeight = 20;
    private const int LevelsPanelPadTop = 28;
    private const int LevelsPanelPadLeft = 6;
    private int _hoveredLevelRow = -1;

    // ── Animation ───────────────────────────────────────────────────────
    private System.Windows.Forms.Timer? _animTimer;
    private double[]? _animFromX, _animFromY;
    private double[]? _animToX, _animToY;
    private int[]? _animMapping;
    private int _animFrame;
    private const int AnimFrames = 20;
    private const int AnimIntervalMs = 16;
    private bool _animatingUp;
    private int _animTargetLevel;

    // ── Viewport animation ──────────────────────────────────────────────
    private System.Windows.Forms.Timer? _vpAnimTimer;
    private float _vpFromPanX, _vpFromPanY, _vpFromZoom;
    private float _vpToPanX, _vpToPanY, _vpToZoom;
    private int _vpAnimFrame;
    private const int VpAnimFrames = 30;
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LodMode { get; set; } = "auto";

    // ── Color mode ──────────────────────────────────────────────────────
    private readonly NodeColorProvider _colorProvider = new();
    private string _nodeColorMode = "community";
    private string _edgeColorMode = "uniform";
    private Rgba32[] _nodeColors = Array.Empty<Rgba32>();
    private Rgba32[] _edgeColors = Array.Empty<Rgba32>();

    // ── Color transition animation ──────────────────────────────────────
    private const int ColorAnimFrames = 20;       // ~330ms at 60fps (16ms ticks)
    private const int ColorAnimIntervalMs = 16;
    private System.Windows.Forms.Timer? _colorAnimTimer;
    private int _colorAnimFrame;
    private Rgba32[]? _prevNodeColors;
    private Rgba32[]? _targetNodeColors;
    private Rgba32[]? _prevEdgeColors;
    private Rgba32[]? _targetEdgeColors;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string NodeColorMode
    {
        get => _nodeColorMode;
        set
        {
            if (_nodeColorMode == value) return;
            _nodeColorMode = value;
            AnimateColorTransition();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EdgeColorMode
    {
        get => _edgeColorMode;
        set
        {
            if (_edgeColorMode == value) return;
            _edgeColorMode = value;
            AnimateColorTransition();
        }
    }

    private void RefreshColors()
    {
        StopColorAnimation();
        _nodeColors = _colorProvider.GetNodeColors(_nodeColorMode);
        _edgeColors = _colorProvider.GetEdgeColors(_edgeColorMode);
    }

    private void AnimateColorTransition()
    {
        if (_graph == null || _graph.NodeCount == 0)
        {
            RefreshColors();
            Invalidate();
            return;
        }

        // Snapshot current display colors as "from"
        _prevNodeColors = (Rgba32[])_nodeColors.Clone();
        _prevEdgeColors = (Rgba32[])_edgeColors.Clone();

        // Compute target colors from new mode
        _targetNodeColors = _colorProvider.GetNodeColors(_nodeColorMode);
        _targetEdgeColors = _colorProvider.GetEdgeColors(_edgeColorMode);

        _colorAnimFrame = 0;
        StopColorAnimation();
        _colorAnimTimer = new System.Windows.Forms.Timer { Interval = ColorAnimIntervalMs };
        _colorAnimTimer.Tick += OnColorAnimTick;
        _colorAnimTimer.Start();
    }

    private void OnColorAnimTick(object? sender, EventArgs e)
    {
        _colorAnimFrame++;
        if (_colorAnimFrame >= ColorAnimFrames)
        {
            FinishColorAnimation();
            return;
        }

        float t = (float)_colorAnimFrame / ColorAnimFrames;
        t = t * t * (3f - 2f * t); // smoothstep

        // Interpolate node colors
        if (_prevNodeColors != null && _targetNodeColors != null)
        {
            int n = Math.Min(_prevNodeColors.Length, _targetNodeColors.Length);
            if (_nodeColors.Length != n) _nodeColors = new Rgba32[n];
            for (int i = 0; i < n; i++)
                _nodeColors[i] = Rgba32.Lerp(_prevNodeColors[i], _targetNodeColors[i], t);
        }

        // Interpolate edge colors
        if (_prevEdgeColors != null && _targetEdgeColors != null)
        {
            int m = Math.Min(_prevEdgeColors.Length, _targetEdgeColors.Length);
            if (_edgeColors.Length != m) _edgeColors = new Rgba32[m];
            for (int i = 0; i < m; i++)
                _edgeColors[i] = Rgba32.Lerp(_prevEdgeColors[i], _targetEdgeColors[i], t);
        }

        Invalidate();
    }

    private void FinishColorAnimation()
    {
        StopColorAnimation();
        _nodeColors = _targetNodeColors ?? _colorProvider.GetNodeColors(_nodeColorMode);
        _edgeColors = _targetEdgeColors ?? _colorProvider.GetEdgeColors(_edgeColorMode);
        _prevNodeColors = _targetNodeColors = null;
        _prevEdgeColors = _targetEdgeColors = null;
        Invalidate();
    }

    private void StopColorAnimation()
    {
        if (_colorAnimTimer != null)
        {
            _colorAnimTimer.Stop();
            _colorAnimTimer.Tick -= OnColorAnimTick;
            _colorAnimTimer.Dispose();
            _colorAnimTimer = null;
        }
    }

    // ── Performance instrumentation ─────────────────────────────────────
    private readonly Stopwatch _frameSw = new();
    private readonly Stopwatch _phaseSw = new();
    private int _perfFrameCount;
    private const double PerfAlpha = 0.1;

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
    private double _lastBlitMs, _avgBlitMs;  // Skia-specific: bitmap→control blit

    private void UpdateEma(ref double avg, double sample)
    {
        if (_perfFrameCount <= 1) avg = sample;
        else avg = avg * (1 - PerfAlpha) + sample * PerfAlpha;
    }

    // ── Skia surface management ─────────────────────────────────────────
    private SKBitmap? _skBitmap;
    private int _skWidth, _skHeight;

    private void EnsureSkiaSurface(int w, int h)
    {
        if (_skBitmap != null && _skWidth == w && _skHeight == h) return;
        _skBitmap?.Dispose();
        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skWidth = w;
        _skHeight = h;
    }

    // ── Palette ─────────────────────────────────────────────────────────
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

    private static readonly SKColor[] SkPalette;

    static SkiaGraphView()
    {
        SkPalette = new SKColor[Palette.Length];
        for (int i = 0; i < Palette.Length; i++)
        {
            var c = Palette[i];
            SkPalette[i] = new SKColor(c.R, c.G, c.B, c.A);
        }
    }

    // ── Pre-computed screen coordinates ─────────────────────────────────
    private float[] _screenX = Array.Empty<float>();
    private float[] _screenY = Array.Empty<float>();

    // ── Layout constants for HUD text ──────────────────────────────────
    private const int HudMarginX = 10;
    private const int HudMarginBottom = 6;
    private const int HintMarginTop = 30;
    private const int HintMarginX = 10;

    public SkiaGraphView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.Selectable, true);
        BackColor = BackgroundColor;
    }

    // ── Graph / hierarchy management ────────────────────────────────────

    /// <summary>
    /// Node count above which parent-highlight ellipses are auto-disabled.
    /// Drawing one ellipse per coarse group is O(coarseN) with allocations;
    /// at scale it hammers the render loop for little visual benefit.
    /// </summary>
    private const int ParentHighlightAutoDisableThreshold = 2000;

    public void SetGraph(GraphModel graph)
    {
        _graph = graph;
        _hierarchy = null;
        _currentLevel = 0;
        _selection.Clear();
        ShowParentHighlight = graph.NodeCount <= ParentHighlightAutoDisableThreshold;
        _colorProvider.SetGraph(graph);
        RefreshColors();
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
        ShowParentHighlight = graph.NodeCount <= ParentHighlightAutoDisableThreshold;
        _colorProvider.SetGraph(graph);
        RefreshColors();
        StopAnimation();
        AutoFit();
        Invalidate();
    }

    public GraphModel? GetGraph() => _graph;
    public CoarseningHierarchy? GetHierarchy() => _hierarchy;

    // ════════════════════════════════════════════════════════════════════
    // ██  RENDERING  ██
    // ════════════════════════════════════════════════════════════════════

    protected override void OnPaint(PaintEventArgs e)
    {
        _frameSw.Restart();

        int w = Width, h = Height;
        if (w < 1 || h < 1) return;

        EnsureSkiaSurface(w, h);
        using var canvas = new SKCanvas(_skBitmap!);

        // Clear
        var bgColor = new SKColor((byte)BackgroundColor.R, (byte)BackgroundColor.G,
                                   (byte)BackgroundColor.B, 255);
        canvas.Clear(bgColor);

        if (_graph == null || _graph.NodeCount == 0)
        {
            DrawPlaceholderSkia(canvas, w, h);
            BlitToGdi(e.Graphics, w, h);
            return;
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
        float viewR = w + 50f, viewB = h + 50f;

        // ── Draw parent highlight ──────────────────────────────────────
        _phaseSw.Restart();
        if (ShowParentHighlight && _hierarchy != null
            && _currentLevel < _hierarchy.LevelCount - 1
            && _animTimer == null)
        {
            DrawParentHighlightSkia(canvas);
        }
        double parentHighlightMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw edges ─────────────────────────────────────────────────
        _phaseSw.Restart();
        if (ShowEdges)
        {
            int edgeAlpha = Math.Clamp((int)(EdgeAlpha * 255), 10, 255);
            bool useEdgeColors = _edgeColorMode != "uniform" && _edgeColors.Length > 0;
            using var edgePaint = new SKPaint
            {
                Color = new SKColor(120, 130, 150, (byte)edgeAlpha),
                StrokeWidth = 1f,
                IsAntialias = AntiAlias,
                Style = SKPaintStyle.Stroke,
            };

            var es = _graph.EdgeSource;
            var et = _graph.EdgeTarget;
            int edgeCount = _graph.EdgeCount;

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

                if (useEdgeColors && ei < _edgeColors.Length)
                {
                    var ec = _edgeColors[ei];
                    // Blend the per-edge alpha with the user's EdgeAlpha slider
                    int a = Math.Clamp((int)(ec.A * EdgeAlpha / 0.3f), 10, 255);
                    edgePaint.Color = new SKColor(ec.R, ec.G, ec.B, (byte)a);
                }

                canvas.DrawLine(ax, ay, bx, by, edgePaint);
            }
        }
        double edgesMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw selected node glow ────────────────────────────────────
        _phaseSw.Restart();
        if (ShowSelectionGlow && _selection.Count > 0)
        {
            using var glowPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 100, 60),
                IsAntialias = AntiAlias,
                Style = SKPaintStyle.Fill,
            };
            float glowR = NodeRadius + 10;
            foreach (int i in _selection)
            {
                if (i >= n) continue;
                float sx = _screenX[i], sy = _screenY[i];
                if (sx < viewL || sx > viewR || sy < viewT || sy > viewB) continue;
                canvas.DrawOval(sx, sy, glowR, glowR, glowPaint);
            }
        }
        double selGlowMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw nodes (LOD-aware, off-screen culling) ─────────────────
        _phaseSw.Restart();
        float r = NodeRadius;

        bool lowDetail;
        if (LodMode == "low") lowDetail = true;
        else if (LodMode == "high") lowDetail = false;
        else lowDetail = (n > 2000 && _zoom < 1.5f);

        int offScreenCount = 0;
        if (ShowNodes)
        {
            using var outlinePaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 200),
                StrokeWidth = 1f,
                IsAntialias = !lowDetail && AntiAlias,
                Style = SKPaintStyle.Stroke,
            };
            using var selOutlinePaint = new SKPaint
            {
                Color = new SKColor(255, 255, 100, 255),
                StrokeWidth = 2f,
                IsAntialias = !lowDetail && AntiAlias,
                Style = SKPaintStyle.Stroke,
            };
            using var fillPaint = new SKPaint
            {
                IsAntialias = !lowDetail && AntiAlias,
                Style = SKPaintStyle.Fill,
            };

            for (int i = 0; i < n; i++)
            {
                float sx = _screenX[i], sy = _screenY[i];
                if (sx < viewL || sx > viewR || sy < viewT || sy > viewB)
                {
                    offScreenCount++;
                    continue;
                }

                float nr = r + Math.Min(degree[i] * 0.3f, 6f);
                var nc = _nodeColors.Length > i ? _nodeColors[i] : new Rgba32(140, 160, 200);
                fillPaint.Color = new SKColor(nc.R, nc.G, nc.B, nc.A);

                if (lowDetail)
                {
                    canvas.DrawRect(sx - nr, sy - nr, nr * 2, nr * 2, fillPaint);
                }
                else
                {
                    canvas.DrawOval(sx, sy, nr, nr, fillPaint);
                    if (ShowOutlines)
                    {
                        if (_selection.Contains(i))
                            canvas.DrawOval(sx, sy, nr, nr, selOutlinePaint);
                        else
                            canvas.DrawOval(sx, sy, nr, nr, outlinePaint);
                    }
                }
            }
        }
        double nodesMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw off-screen indicators ─────────────────────────────────
        _phaseSw.Restart();
        if (ShowOffScreenIndicators && offScreenCount > 0 && offScreenCount < 2000)
            DrawOffScreenIndicatorsSkia(canvas, w, h);
        double offScreenMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw labels ────────────────────────────────────────────────
        _phaseSw.Restart();
        if (ShowLabels && _zoom > 0.5f)
        {
            using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 11f);
            using var labelPaint = new SKPaint
            {
                Color = new SKColor(220, 220, 230, 200),
                IsAntialias = true,
            };

            var labels = _graph.Labels;
            for (int i = 0; i < n; i++)
            {
                float sx = _screenX[i], sy = _screenY[i];
                if (sx < viewL || sx > viewR || sy < viewT || sy > viewB) continue;

                float nr = r + Math.Min(degree[i] * 0.3f, 6f);
                canvas.DrawText(labels[i], sx + nr + 2, sy + 4, SKTextAlign.Left, labelFont, labelPaint);
            }
        }
        double labelsMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw selection rectangle ───────────────────────────────────
        if (_selecting)
        {
            var rect = GetSelectionRect();
            using var selFillPaint = new SKPaint
            {
                Color = new SKColor(100, 200, 255, 30),
                Style = SKPaintStyle.Fill,
            };
            using var selStrokePaint = new SKPaint
            {
                Color = new SKColor(100, 200, 255, 180),
                StrokeWidth = 1f,
                Style = SKPaintStyle.Stroke,
                PathEffect = SKPathEffect.CreateDash(new[] { 6f, 3f }, 0),
                IsAntialias = true,
            };
            var skRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            canvas.DrawRect(skRect, selFillPaint);
            canvas.DrawRect(skRect, selStrokePaint);
        }

        // ── Draw minimap ───────────────────────────────────────────────
        _phaseSw.Restart();
        if (ShowMinimap)
            DrawMinimapSkia(canvas, w, h);
        double minimapMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Draw coarsening levels panel ───────────────────────────────
        _phaseSw.Restart();
        if (ShowLevelsPanel && _hierarchy != null && _hierarchy.LevelCount > 1)
            DrawLevelsPanelSkia(canvas);
        double levelsPanelMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── HUD ────────────────────────────────────────────────────────
        _phaseSw.Restart();
        DrawHudSkia(canvas, w, h);
        double hudMs = _phaseSw.Elapsed.TotalMilliseconds;

        // ── Blit Skia bitmap to GDI+ surface (tracked separately) ──────
        _phaseSw.Restart();
        BlitToGdi(e.Graphics, w, h);
        double blitMs = _phaseSw.Elapsed.TotalMilliseconds;

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
        _lastBlitMs = blitMs;
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
        UpdateEma(ref _avgBlitMs, blitMs);
    }

    // ── Blit the SKBitmap to the WinForms Graphics surface ──────────────

    private void BlitToGdi(Graphics g, int w, int h)
    {
        if (_skBitmap == null) return;

        // Pin the SKBitmap pixels and wrap in a System.Drawing.Bitmap
        var info = _skBitmap.Info;
        var ptr = _skBitmap.GetPixels();
        using var bmp = new Bitmap(info.Width, info.Height, info.RowBytes,
            PixelFormat.Format32bppPArgb, ptr);
        g.DrawImageUnscaled(bmp, 0, 0);
    }

    // ════════════════════════════════════════════════════════════════════
    // ██  SKIA DRAWING METHODS  ██
    // ════════════════════════════════════════════════════════════════════

    private void DrawPlaceholderSkia(SKCanvas c, int w, int h)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 20f);
        using var paint = new SKPaint
        {
            Color = new SKColor(180, 180, 200, 100),
            IsAntialias = true,
        };
        string text = "◇  No graph loaded  ◇";
        float textW = font.MeasureText(text);
        c.DrawText(text, (w - textW) / 2, h / 2f + 7, SKTextAlign.Left, font, paint);
    }

    private void DrawParentHighlightSkia(SKCanvas c)
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

        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = AntiAlias };
        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = AntiAlias,
            PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0),
        };

        for (int ci = 0; ci < coarseN; ci++)
        {
            if (counts[ci] <= 1) continue;

            float pad = NodeRadius + 8;
            float cx = (minXs[ci] + maxXs[ci]) / 2;
            float cy = (minYs[ci] + maxYs[ci]) / 2;
            float rx = (maxXs[ci] - minXs[ci]) / 2 + pad;
            float ry = (maxYs[ci] - minYs[ci]) / 2 + pad;

            var pc = Palette[coarseGraph.Community[ci] % Palette.Length];
            fillPaint.Color = new SKColor(pc.R, pc.G, pc.B, 25);
            strokePaint.Color = new SKColor(pc.R, pc.G, pc.B, 50);

            c.DrawOval(cx, cy, rx, ry, fillPaint);
            c.DrawOval(cx, cy, rx, ry, strokePaint);
        }
    }

    private void DrawOffScreenIndicatorsSkia(SKCanvas c, int w, int h)
    {
        if (_graph == null) return;
        int n = _graph.NodeCount;
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 200, 80, 120),
            IsAntialias = AntiAlias,
            Style = SKPaintStyle.Fill,
        };
        float dotR = 3f;

        for (int i = 0; i < n; i++)
        {
            float sx = _screenX[i], sy = _screenY[i];
            if (sx >= -50 && sx <= w + 50 && sy >= -50 && sy <= h + 50) continue;

            float cx = Math.Clamp(sx, 2, w - 2);
            float cy = Math.Clamp(sy, 2, h - 2);
            c.DrawOval(cx, cy, dotR, dotR, paint);
        }
    }

    private void DrawMinimapSkia(SKCanvas c, int w, int h)
    {
        if (_graph == null || _graph.NodeCount == 0) return;

        int n = _graph.NodeCount;
        var npx = _graph.NodeX;
        var npy = _graph.NodeY;

        double wMinX = double.MaxValue, wMinY = double.MaxValue;
        double wMaxX = double.MinValue, wMaxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (npx[i] < wMinX) wMinX = npx[i];
            if (npy[i] < wMinY) wMinY = npy[i];
            if (npx[i] > wMaxX) wMaxX = npx[i];
            if (npy[i] > wMaxY) wMaxY = npy[i];
        }
        double wW = wMaxX - wMinX; if (wW < 1) wW = 1;
        double wH = wMaxY - wMinY; if (wH < 1) wH = 1;

        int mmX = w - MinimapSize - MinimapMargin;
        int mmY = h - MinimapSize - MinimapMargin - 32;
        int mmW = MinimapSize, mmH = MinimapSize;

        double scale = Math.Min((double)(mmW - 8) / wW, (double)(mmH - 8) / wH);
        double offX = mmX + 4 + ((mmW - 8) - wW * scale) / 2;
        double offY = mmY + 4 + ((mmH - 8) - wH * scale) / 2;

        using var bgPaint = new SKPaint { Color = new SKColor(16, 16, 24, 180), Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = new SKColor(100, 110, 140, 100), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        c.DrawRect(mmX, mmY, mmW, mmH, bgPaint);
        c.DrawRect(mmX, mmY, mmW, mmH, borderPaint);

        using var nodePaint = new SKPaint { Color = new SKColor(140, 160, 200, 160), Style = SKPaintStyle.Fill };
        for (int i = 0; i < n; i++)
        {
            float mx = (float)((npx[i] - wMinX) * scale + offX);
            float my = (float)((npy[i] - wMinY) * scale + offY);
            c.DrawRect(mx, my, 1.5f, 1.5f, nodePaint);
        }

        // Viewport rectangle
        var (vwTL_x, vwTL_y) = ScreenToWorld(0, 0);
        var (vwBR_x, vwBR_y) = ScreenToWorld(w, h);
        float vx1 = Math.Max((float)((vwTL_x - wMinX) * scale + offX), mmX);
        float vy1 = Math.Max((float)((vwTL_y - wMinY) * scale + offY), mmY);
        float vx2 = Math.Min((float)((vwBR_x - wMinX) * scale + offX), mmX + mmW);
        float vy2 = Math.Min((float)((vwBR_y - wMinY) * scale + offY), mmY + mmH);
        if (vx2 > vx1 && vy2 > vy1)
        {
            using var vpFill = new SKPaint { Color = new SKColor(255, 255, 255, 20), Style = SKPaintStyle.Fill };
            using var vpStroke = new SKPaint { Color = new SKColor(255, 255, 255, 180), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            c.DrawRect(vx1, vy1, vx2 - vx1, vy2 - vy1, vpFill);
            c.DrawRect(vx1, vy1, vx2 - vx1, vy2 - vy1, vpStroke);
        }

        // Label
        using var mmFont = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono"), 10f);
        using var labelPaint = new SKPaint
        {
            Color = new SKColor(160, 160, 180, 120),
        };
        c.DrawText("minimap", mmX + 3, mmY + 11, SKTextAlign.Left, mmFont, labelPaint);
    }

    private void DrawLevelsPanelSkia(SKCanvas c)
    {
        if (_hierarchy == null) return;

        int levelCount = _hierarchy.LevelCount;
        int panelH = LevelsPanelPadTop + levelCount * LevelRowHeight + 8;
        int panelW = LevelsPanelWidth;
        int px = LevelsPanelPadLeft;
        int py = 32;

        using var bgPaint = new SKPaint { Color = new SKColor(16, 16, 24, 200), Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = new SKColor(100, 110, 140, 80), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        c.DrawRect(px, py, panelW, panelH, bgPaint);
        c.DrawRect(px, py, panelW, panelH, borderPaint);

        // Header
        using var headerFont = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono"), 12f) { Embolden = true };
        using var headerPaint = new SKPaint
        {
            Color = new SKColor(200, 210, 230, 200),
        };
        c.DrawText("Coarsening Levels", px + 6, py + 14, SKTextAlign.Left, headerFont, headerPaint);

        // Column headers
        using var colFont = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono"), 10f);
        using var colPaint = new SKPaint
        {
            Color = new SKColor(160, 160, 180, 120),
        };
        c.DrawText("Lvl   Nodes   Edges    Memory", px + 6, py + 26, SKTextAlign.Left, colFont, colPaint);

        // Rows
        using var rowFont = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono"), 11f);
        using var rowPaint = new SKPaint();

        for (int i = 0; i < levelCount; i++)
        {
            int rowY = py + LevelsPanelPadTop + i * LevelRowHeight;
            bool isCurrent = (i == _currentLevel);
            bool isHovered = (i == _hoveredLevelRow);

            if (isCurrent)
            {
                using var bg = new SKPaint { Color = new SKColor(102, 194, 255, 30), Style = SKPaintStyle.Fill };
                c.DrawRect(px + 1, rowY, panelW - 2, LevelRowHeight, bg);
            }
            else if (isHovered)
            {
                using var bg = new SKPaint { Color = new SKColor(100, 200, 255, 40), Style = SKPaintStyle.Fill };
                c.DrawRect(px + 1, rowY, panelW - 2, LevelRowHeight, bg);
            }

            var graph = _hierarchy.Graphs[i];
            long memBytes = graph.EstimateMemoryBytes();
            string memStr = FormatBytes(memBytes);
            string line = $" {i,2}  {graph.NodeCount,6}  {graph.EdgeCount,6}  {memStr,8}";

            rowPaint.Color = isCurrent
                ? new SKColor(102, 194, 255, 255)
                : new SKColor(180, 190, 210, 180);
            c.DrawText(line, px + 4, rowY + 14, SKTextAlign.Left, rowFont, rowPaint);

            if (isCurrent)
                c.DrawText("▶", px + panelW - 20, rowY + 14, SKTextAlign.Left, rowFont, rowPaint);
        }
    }

    private void DrawHudSkia(SKCanvas c, int w, int h)
    {
        if (_graph == null) return;

        string info = $"{_graph.Title}  —  {_graph.NodeCount}n, {_graph.EdgeCount}e";
        if (_selection.Count > 0) info += $"  |  {_selection.Count} selected";

        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            info += $"  |  Level {_currentLevel}/{_hierarchy.LevelCount - 1}";
            if (_currentLevel == 0) info += " (finest)";
            else if (_currentLevel == _hierarchy.LevelCount - 1) info += " (coarsest)";
        }

        if (_perfFrameCount > 3)
            info += $"  |  {_avgFrameMs:F1}ms  [Skia]";

        using var hudFont = new SKFont(SKTypeface.FromFamilyName("Cascadia Mono"), 13f);
        using var paint = new SKPaint
        {
            Color = new SKColor(200, 200, 220, 160),
            IsAntialias = true,
        };

        c.DrawText(info, HudMarginX, h - HudMarginBottom, SKTextAlign.Left, hudFont, paint);

        if (_hierarchy != null && _hierarchy.LevelCount > 1)
        {
            string hint = "PgUp/PgDn: levels  |  Home: fit view  |  +/−: zoom  |  Arrows: pan";
            float hintW = hudFont.MeasureText(hint);
            c.DrawText(hint, w - hintW - HintMarginX, HintMarginTop + 13, SKTextAlign.Left, hudFont, paint);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // ██  SHARED LOGIC (adapted from GraphView)  ██
    // ════════════════════════════════════════════════════════════════════

    private static bool LineIntersectsRect(PointF a, PointF b, RectangleF rect)
    {
        // Cohen-Sutherland clip test
        float xmin = rect.Left, xmax = rect.Right, ymin = rect.Top, ymax = rect.Bottom;
        float x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;

        int code0 = OutCode(x0, y0), code1 = OutCode(x1, y1);
        while (true)
        {
            if ((code0 | code1) == 0) return true;
            if ((code0 & code1) != 0) return false;
            int codeOut = code0 != 0 ? code0 : code1;
            float x = 0, y = 0;
            if ((codeOut & 8) != 0) { x = x0 + (x1 - x0) * (ymax - y0) / (y1 - y0); y = ymax; }
            else if ((codeOut & 4) != 0) { x = x0 + (x1 - x0) * (ymin - y0) / (y1 - y0); y = ymin; }
            else if ((codeOut & 2) != 0) { y = y0 + (y1 - y0) * (xmax - x0) / (x1 - x0); x = xmax; }
            else if ((codeOut & 1) != 0) { y = y0 + (y1 - y0) * (xmin - x0) / (x1 - x0); x = xmin; }
            if (codeOut == code0) { x0 = x; y0 = y; code0 = OutCode(x0, y0); }
            else { x1 = x; y1 = y; code1 = OutCode(x1, y1); }
        }

        int OutCode(float px, float py)
        {
            int code = 0;
            if (px < xmin) code |= 1;
            else if (px > xmax) code |= 2;
            if (py < ymin) code |= 4;
            else if (py > ymax) code |= 8;
            return code;
        }
    }

    // ── Coordinate transforms ───────────────────────────────────────────

    public PointF WorldToScreen(double wx, double wy) =>
        new((float)(wx * _zoom + _pan.X), (float)(wy * _zoom + _pan.Y));

    public (double wx, double wy) ScreenToWorld(float sx, float sy) =>
        ((sx - _pan.X) / _zoom, (sy - _pan.Y) / _zoom);

    // ── Auto-fit ────────────────────────────────────────────────────────

    public void AutoFit()
    {
        if (_graph == null || _graph.NodeCount == 0) return;

        int n = _graph.NodeCount;
        var npx = _graph.NodeX;
        var npy = _graph.NodeY;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        for (int i = 0; i < n; i++)
        {
            if (npx[i] < minX) minX = npx[i];
            if (npy[i] < minY) minY = npy[i];
            if (npx[i] > maxX) maxX = npx[i];
            if (npy[i] > maxY) maxY = npy[i];
        }

        double graphW = maxX - minX;
        double graphH = maxY - minY;
        if (graphW < 1) graphW = 1;
        if (graphH < 1) graphH = 1;

        float margin = 60;
        float viewW = Width - margin * 2;
        float viewH = Height - margin - 50;
        if (viewW < 1) viewW = 1;
        if (viewH < 1) viewH = 1;

        _zoom = (float)Math.Min(viewW / graphW, viewH / graphH);
        _pan = new PointF(
            (float)(margin - minX * _zoom + (viewW - graphW * _zoom) / 2),
            (float)(margin - minY * _zoom + (viewH - graphH * _zoom) / 2));
    }

    // ── Selection management ────────────────────────────────────────────

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

    private Rectangle GetSelectionRect()
    {
        int x1 = Math.Min(_selStart.X, _selEnd.X);
        int y1 = Math.Min(_selStart.Y, _selEnd.Y);
        int x2 = Math.Max(_selStart.X, _selEnd.X);
        int y2 = Math.Max(_selStart.Y, _selEnd.Y);
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    // ── Level navigation ────────────────────────────────────────────────

    public void SetLevel(int level)
    {
        if (_hierarchy == null) return;
        level = Math.Clamp(level, 0, _hierarchy.LevelCount - 1);
        if (level == _currentLevel) return;

        if (Math.Abs(level - _currentLevel) == 1)
        {
            if (level > _currentLevel) GoCoarser();
            else GoFiner();
        }
        else
        {
            StopAnimation();
            StopVpAnimation();
            _currentLevel = level;
            _graph = _hierarchy.Graphs[level];
            _selection.Clear();
            _colorProvider.SetGraph(_graph);
            RefreshColors();
            AutoFit();
            Invalidate();
            LevelChanged?.Invoke();
        }
    }

    public void GoCoarser()
    {
        if (_hierarchy == null) return;
        int target = _currentLevel + 1;
        if (target >= _hierarchy.LevelCount) return;
        StartLevelAnimation(target, true);
    }

    public void GoFiner()
    {
        if (_hierarchy == null) return;
        int target = _currentLevel - 1;
        if (target < 0) return;
        StartLevelAnimation(target, false);
    }

    private void StartLevelAnimation(int target, bool goingUp)
    {
        StopAnimation();
        StopVpAnimation();

        _animatingUp = goingUp;
        _animTargetLevel = target;

        int fromLevel = _currentLevel;
        int toLevel = target;

        var fromGraph = _hierarchy!.Graphs[fromLevel];
        var toGraph = _hierarchy.Graphs[toLevel];

        _animFromX = (double[])fromGraph.NodeX.Clone();
        _animFromY = (double[])fromGraph.NodeY.Clone();
        _animToX = (double[])toGraph.NodeX.Clone();
        _animToY = (double[])toGraph.NodeY.Clone();

        if (goingUp)
        {
            _animMapping = _hierarchy.FineToCoarse[fromLevel];
        }
        else
        {
            _animMapping = _hierarchy.FineToCoarse[toLevel];
        }

        _animFrame = 0;
        _animTimer = new System.Windows.Forms.Timer { Interval = AnimIntervalMs };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_graph == null || _hierarchy == null)
        {
            StopAnimation();
            return;
        }

        _animFrame++;
        if (_animFrame >= AnimFrames)
        {
            StopAnimation();
            FinishLevelTransition();
            return;
        }

        double t = (double)_animFrame / AnimFrames;
        t = t * t * (3.0 - 2.0 * t); // smoothstep

        if (_animatingUp)
        {
            int n = _graph.NodeCount;
            for (int i = 0; i < n; i++)
            {
                int ci = _animMapping![i];
                _graph.NodeX[i] = _animFromX![i] * (1 - t) + _animToX![ci] * t;
                _graph.NodeY[i] = _animFromY![i] * (1 - t) + _animToY![ci] * t;
            }
        }
        else
        {
            var toGraph = _hierarchy.Graphs[_animTargetLevel];
            int n = toGraph.NodeCount;
            _graph = toGraph;
            for (int i = 0; i < n; i++)
            {
                int ci = _animMapping![i];
                _graph.NodeX[i] = _animFromX![ci] * (1 - t) + _animToX![i] * t;
                _graph.NodeY[i] = _animFromY![ci] * (1 - t) + _animToY![i] * t;
            }
        }
        Invalidate();
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
    }

    private void FinishLevelTransition()
    {
        _currentLevel = _animTargetLevel;
        _graph = _hierarchy!.Graphs[_currentLevel];
        _colorProvider.SetGraph(_graph);
        RefreshColors();

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

        _vpFromPanX = _pan.X;
        _vpFromPanY = _pan.Y;
        _vpFromZoom = _zoom;

        AutoFit();

        _vpToPanX = _pan.X;
        _vpToPanY = _pan.Y;
        _vpToZoom = _zoom;

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
        t = t * t * (3.0 - 2.0 * t);
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

    // ── Helper ──────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}K";
        return $"{bytes / (1024.0 * 1024.0):F1}M";
    }

    // ── Mouse interaction ───────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            // Check levels panel click
            int lvl = HitTestLevelRow(e.Location);
            if (lvl >= 0)
            {
                SetLevel(lvl);
                base.OnMouseDown(e);
                return;
            }

            // Start rubber-band selection
            _selecting = true;
            _selStart = e.Location;
            _selEnd = e.Location;
        }
        else if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            _panning = true;
            _lastMouse = e.Location;
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
            int newHovered = HitTestLevelRow(e.Location);
            if (newHovered != _hoveredLevelRow)
            {
                _hoveredLevelRow = newHovered;
                Invalidate();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
        }
        else if (_selecting)
        {
            _selecting = false;
            var rect = GetSelectionRect();
            if (rect.Width > 3 && rect.Height > 3 && _graph != null)
            {
                if ((ModifierKeys & Keys.Control) == 0) _selection.Clear();
                int n = _graph.NodeCount;
                for (int i = 0; i < n; i++)
                {
                    float sx = _screenX[i], sy = _screenY[i];
                    if (rect.Contains((int)sx, (int)sy)) _selection.Add(i);
                }
                SelectionChanged?.Invoke();
            }
            Invalidate();
        }
        base.OnMouseUp(e);
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
            case Keys.PageUp: GoCoarser(); e.Handled = true; break;
            case Keys.PageDown: GoFiner(); e.Handled = true; break;
            case Keys.D0 when e.Modifiers == Keys.None: SetLevel(0); e.Handled = true; break;
            case Keys.D9 when e.Modifiers == Keys.None:
                if (_hierarchy != null) SetLevel(_hierarchy.LevelCount - 1);
                e.Handled = true; break;
            case Keys.Home:
            case Keys.F:
                AutoFit(); Invalidate(); e.Handled = true; break;
            case Keys.End:
                if (_hierarchy != null) SetLevel(_hierarchy.LevelCount - 1);
                e.Handled = true; break;
            case Keys.Oemplus or Keys.Add:
                ZoomCenter(KeyboardZoomFactor); e.Handled = true; break;
            case Keys.OemMinus or Keys.Subtract:
                ZoomCenter(1f / KeyboardZoomFactor); e.Handled = true; break;
            case Keys.Left: _pan.X += KeyboardPanStep; Invalidate(); e.Handled = true; break;
            case Keys.Right: _pan.X -= KeyboardPanStep; Invalidate(); e.Handled = true; break;
            case Keys.Up: _pan.Y += KeyboardPanStep; Invalidate(); e.Handled = true; break;
            case Keys.Down: _pan.Y -= KeyboardPanStep; Invalidate(); e.Handled = true; break;
            case Keys.Escape: ClearSelection(); e.Handled = true; break;
            case Keys.A when e.Modifiers == Keys.Control: SelectAll(); e.Handled = true; break;
            case Keys.L when e.Modifiers == Keys.None:
                ShowLabels = !ShowLabels; Invalidate(); e.Handled = true; break;
            case Keys.M when e.Modifiers == Keys.None:
                ShowMinimap = !ShowMinimap; Invalidate(); e.Handled = true; break;
            case Keys.E when e.Modifiers == Keys.None:
                ShowEdges = !ShowEdges; Invalidate(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

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

    // ── Levels panel hit-test ──────────────────────────────────────────

    private Rectangle GetLevelsPanelRect()
    {
        if (_hierarchy == null) return Rectangle.Empty;
        int levelCount = _hierarchy.LevelCount;
        int panelH = LevelsPanelPadTop + levelCount * LevelRowHeight + 8;
        return new Rectangle(LevelsPanelPadLeft, 32, LevelsPanelWidth, panelH);
    }

    private int HitTestLevelRow(Point pt)
    {
        if (_hierarchy == null || !ShowLevelsPanel) return -1;
        var panelRect = GetLevelsPanelRect();
        if (!panelRect.Contains(pt)) return -1;
        int rowIdx = (pt.Y - panelRect.Y - LevelsPanelPadTop) / LevelRowHeight;
        if (rowIdx < 0 || rowIdx >= _hierarchy.LevelCount) return -1;
        return rowIdx;
    }

    // ── Performance stats query ─────────────────────────────────────────

    public Dictionary<string, object> GetRenderStats()
    {
        return new Dictionary<string, object>
        {
            ["renderer"] = "skia",
            ["frame_count"] = _perfFrameCount,
            ["last_frame_ms"] = Math.Round(_lastFrameMs, 3),
            ["avg_frame_ms"] = Math.Round(_avgFrameMs, 3),
            ["phases"] = new Dictionary<string, object>
            {
                ["precompute"] = new { last = Math.Round(_lastPrecomputeMs, 3), avg = Math.Round(_avgPrecomputeMs, 3) },
                ["edges"] = new { last = Math.Round(_lastEdgesMs, 3), avg = Math.Round(_avgEdgesMs, 3) },
                ["nodes"] = new { last = Math.Round(_lastNodesMs, 3), avg = Math.Round(_avgNodesMs, 3) },
                ["parent_highlight"] = new { last = Math.Round(_lastParentHighlightMs, 3), avg = Math.Round(_avgParentHighlightMs, 3) },
                ["minimap"] = new { last = Math.Round(_lastMinimapMs, 3), avg = Math.Round(_avgMinimapMs, 3) },
                ["levels_panel"] = new { last = Math.Round(_lastLevelsPanelMs, 3), avg = Math.Round(_avgLevelsPanelMs, 3) },
                ["off_screen"] = new { last = Math.Round(_lastOffScreenMs, 3), avg = Math.Round(_avgOffScreenMs, 3) },
                ["labels"] = new { last = Math.Round(_lastLabelsMs, 3), avg = Math.Round(_avgLabelsMs, 3) },
                ["hud"] = new { last = Math.Round(_lastHudMs, 3), avg = Math.Round(_avgHudMs, 3) },
                ["sel_glow"] = new { last = Math.Round(_lastSelGlowMs, 3), avg = Math.Round(_avgSelGlowMs, 3) },
                ["blit"] = new { last = Math.Round(_lastBlitMs, 3), avg = Math.Round(_avgBlitMs, 3) },
            },
            ["settings"] = new Dictionary<string, object>
            {
                ["show_edges"] = ShowEdges,
                ["show_nodes"] = ShowNodes,
                ["show_outlines"] = ShowOutlines,
                ["show_parent_highlight"] = ShowParentHighlight,
                ["show_minimap"] = ShowMinimap,
                ["show_off_screen"] = ShowOffScreenIndicators,
                ["show_selection_glow"] = ShowSelectionGlow,
                ["show_labels"] = ShowLabels,
                ["anti_alias"] = AntiAlias,
                ["lod_mode"] = LodMode,
            },
        };
    }

    public void ResetRenderStats()
    {
        _perfFrameCount = 0;
        _avgFrameMs = _avgPrecomputeMs = _avgEdgesMs = _avgNodesMs = 0;
        _avgParentHighlightMs = _avgMinimapMs = _avgLevelsPanelMs = 0;
        _avgOffScreenMs = _avgLabelsMs = _avgHudMs = _avgSelGlowMs = _avgBlitMs = 0;
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAnimation();
            StopVpAnimation();
            _skBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
