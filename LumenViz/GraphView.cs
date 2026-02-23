using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LumenViz;

/// <summary>
/// Custom WinForms control that renders a graph with force-directed layout.
/// Supports zoom, pan, community coloring, and node labels.
/// </summary>
public class GraphView : Control
{
    private GraphModel? _graph;
    private PointF _pan = PointF.Empty;
    private float _zoom = 1.0f;
    private Point _lastMouse;
    private bool _dragging;

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

    public GraphView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = BackgroundColor;
    }

    /// <summary>
    /// Load a graph model and auto-fit it to the view.
    /// </summary>
    public void SetGraph(GraphModel graph)
    {
        _graph = graph;
        AutoFit();
        Invalidate();
    }

    public GraphModel? GetGraph() => _graph;

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

        // ── Pre-cache palette brushes ──────────────────────────────────
        var paletteBrushes = new SolidBrush[Palette.Length];
        for (int pi = 0; pi < Palette.Length; pi++)
            paletteBrushes[pi] = new SolidBrush(Palette[pi]);

        // ── Draw nodes ─────────────────────────────────────────────────
        float r = NodeRadius;
        using var outlinePen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f);

        var community = _graph.Community;
        for (int i = 0; i < n; i++)
        {
            var pt = WorldToScreen(px[i], py[i]);
            float nr = r + Math.Min(degree[i] * 0.3f, 6f);

            g.FillEllipse(paletteBrushes[community[i] % Palette.Length],
                pt.X - nr, pt.Y - nr, nr * 2, nr * 2);
            g.DrawEllipse(outlinePen,
                pt.X - nr, pt.Y - nr, nr * 2, nr * 2);
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

        // Dispose palette brushes
        foreach (var brush in paletteBrushes) brush.Dispose();

        // ── HUD ────────────────────────────────────────────────────────
        DrawHud(g);
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

        string info = $"{_graph.Title}  —  {_graph.NodeCount} nodes, {_graph.EdgeCount} edges";
        using var font = new Font("Cascadia Mono", 9f);
        using var brush = new SolidBrush(Color.FromArgb(160, 200, 200, 220));
        g.DrawString(info, font, brush, 8, Height - 24);
    }

    // ── Coordinate transforms ───────────────────────────────────────────

    /// <summary>Convert world coordinates to screen coordinates.</summary>
    public PointF WorldToScreen(double wx, double wy)
    {
        return new PointF(
            (float)(wx * _zoom + _pan.X),
            (float)(wy * _zoom + _pan.Y));
    }

    /// <summary>Convert world coordinates for node i to screen.</summary>
    public PointF WorldToScreenNode(int i)
    {
        if (_graph == null) return PointF.Empty;
        return WorldToScreen(_graph.NodeX[i], _graph.NodeY[i]);
    }

    // ── Mouse interaction: pan & zoom ───────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
        {
            _dragging = true;
            _lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _pan.X += e.X - _lastMouse.X;
            _pan.Y += e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Cursor = Cursors.Default;
        base.OnMouseUp(e);
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
}
