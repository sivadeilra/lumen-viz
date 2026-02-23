using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// A managed visualization window. Created and controlled via MCP tools.
/// Each window has a unique ID, an <see cref="IGraphViewer"/> (which is
/// a docked Control), a status bar, and an interaction event queue.
///
/// The viewer type can be switched at runtime (e.g. force → matrix)
/// via <see cref="SetViewerType"/>. Graph state is transferred automatically.
/// </summary>
public class VizWindow : Form
{
    private readonly string _windowId;
    private IGraphViewer _viewer;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly MenuStrip _menuStrip;
    private readonly ToolStripMenuItem _showLabelsItem;

    /// <summary>
    /// Thread-safe queue of user interaction events (node clicks, key
    /// presses, window resize, etc.). Polled by MCP `get_interactions` tool.
    /// </summary>
    private readonly ConcurrentQueue<JsonObject> _interactions = new();

    /// <summary>Fired when the user closes this window.</summary>
    public event Action<string>? WindowClosed;

    public string WindowId => _windowId;

    /// <summary>The active graph viewer (always also a Control).</summary>
    public IGraphViewer Viewer => _viewer;

    /// <summary>The active viewer as a WinForms Control (for Invalidate, Focus, etc.).</summary>
    public Control ViewerControl => (Control)_viewer;

    public VizWindow(string id, string title, int width, int height,
                     string viewerType = "force")
    {
        _windowId = id;

        // ── Menu strip ──────────────────────────────────────────────────
        _menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        exitItem.ShortcutKeys = Keys.Alt | Keys.F4;
        fileMenu.DropDownItems.Add(exitItem);
        _menuStrip.Items.Add(fileMenu);

        // ── View menu ───────────────────────────────────────────────────
        var viewMenu = new ToolStripMenuItem("&View");

        _showLabelsItem = new ToolStripMenuItem("Show &Labels");
        _showLabelsItem.ShortcutKeyDisplayString = "L";
        _showLabelsItem.CheckOnClick = true;
        _showLabelsItem.CheckedChanged += (_, _) =>
        {
            _viewer.ShowLabels = _showLabelsItem.Checked;
            ViewerControl.Invalidate();
        };
        viewMenu.DropDownItems.Add(_showLabelsItem);

        var showEdgesItem = new ToolStripMenuItem("Show &Edges");
        showEdgesItem.ShortcutKeyDisplayString = "E";
        showEdgesItem.Checked = true;
        showEdgesItem.CheckOnClick = true;
        showEdgesItem.CheckedChanged += (_, _) =>
        {
            _viewer.ShowEdges = showEdgesItem.Checked;
            ViewerControl.Invalidate();
        };
        viewMenu.DropDownItems.Add(showEdgesItem);

        var showMinimapItem = new ToolStripMenuItem("Show &Minimap");
        showMinimapItem.ShortcutKeyDisplayString = "M";
        showMinimapItem.Checked = true;
        showMinimapItem.CheckOnClick = true;
        showMinimapItem.CheckedChanged += (_, _) =>
        {
            _viewer.ShowMinimap = showMinimapItem.Checked;
            ViewerControl.Invalidate();
        };
        viewMenu.DropDownItems.Add(showMinimapItem);

        // Viewer type sub-menu
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        var viewerTypeMenu = new ToolStripMenuItem("Viewer &Type");
        foreach (var (label, type) in new[] { ("Force Layout", "force"), ("Adjacency Matrix", "matrix") })
        {
            var item = new ToolStripMenuItem(label) { Tag = type };
            item.Click += (_, _) => SetViewerType(type);
            viewerTypeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(viewerTypeMenu);

        _menuStrip.Items.Add(viewMenu);

        // ── Color menu ──────────────────────────────────────────────────
        var colorMenu = new ToolStripMenuItem("&Color");

        var nodeColorMenu = new ToolStripMenuItem("&Node Color");
        var nodeColorItems = new (string label, string mode)[]
        {
            ("Community", "community"),
            ("Degree Centrality", "degree"),
            ("In/Out Ratio (directed)", "in_out_ratio"),
            ("Betweenness Centrality", "betweenness"),
            ("PageRank (directed)", "pagerank"),
            ("Clustering Coefficient", "clustering"),
            ("K-Core Shell", "kcore"),
        };
        foreach (var (label, mode) in nodeColorItems)
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) =>
            {
                _viewer.NodeColorMode = mode;
                UpdateColorMenuChecks(nodeColorMenu, mode);
            };
            if (mode == "community") item.Checked = true;
            nodeColorMenu.DropDownItems.Add(item);
        }
        colorMenu.DropDownItems.Add(nodeColorMenu);

        var edgeColorMenu = new ToolStripMenuItem("&Edge Color");
        var edgeColorItems = new (string label, string mode)[]
        {
            ("Uniform", "uniform"),
            ("Community (intra/inter)", "community"),
            ("Edge Weight", "weight"),
            ("Reciprocity (directed)", "reciprocity"),
            ("Bridge Edges", "bridge"),
        };
        foreach (var (label, mode) in edgeColorItems)
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) =>
            {
                _viewer.EdgeColorMode = mode;
                UpdateColorMenuChecks(edgeColorMenu, mode);
            };
            if (mode == "uniform") item.Checked = true;
            edgeColorMenu.DropDownItems.Add(item);
        }
        colorMenu.DropDownItems.Add(edgeColorMenu);

        _menuStrip.Items.Add(colorMenu);

        // ── Create the initial viewer ───────────────────────────────────
        _viewer = CreateViewer(viewerType);
        var ctrl = (Control)_viewer;
        ctrl.Dock = DockStyle.Fill;
        WireViewerEvents();

        // ── Status bar ──────────────────────────────────────────────────
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel($"◇  {id}  —  Ready");
        _statusBar.Items.Add(_statusLabel);

        // ── Form setup ──────────────────────────────────────────────────
        Text = title;
        ClientSize = new Size(width, height);
        MainMenuStrip = _menuStrip;
        Controls.Add(ctrl);
        Controls.Add(_menuStrip);
        Controls.Add(_statusBar);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Wire up interaction tracking ────────────────────────────────
        KeyPreview = true;
        KeyDown += OnWindowKeyDown;
        Resize += OnWindowResize;
        FormClosed += OnWindowClosed;
    }

    // ════════════════════════════════════════════════════════════════════
    // Viewer factory and switching
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Create a new viewer instance of the given type.</summary>
    public static IGraphViewer CreateViewer(string viewerType)
    {
        return viewerType switch
        {
            "matrix" => new MatrixView(),
            _ => new SkiaGraphView(),
        };
    }

    /// <summary>
    /// Switch to a different viewer type. Transfers graph/hierarchy state.
    /// </summary>
    public void SetViewerType(string viewerType)
    {
        if (_viewer.ViewerType == viewerType) return;

        // Capture state from old viewer
        var graph = _viewer.GetGraph();
        var hierarchy = _viewer.GetHierarchy();
        var selection = _viewer.Selection.ToArray();

        // Unwire old viewer
        UnwireViewerEvents();
        var oldCtrl = (Control)_viewer;
        Controls.Remove(oldCtrl);
        oldCtrl.Dispose();

        // Create and install new viewer
        _viewer = CreateViewer(viewerType);
        var newCtrl = (Control)_viewer;
        newCtrl.Dock = DockStyle.Fill;
        Controls.Add(newCtrl);
        newCtrl.SendToBack();
        WireViewerEvents();

        // Transfer state
        if (graph != null && hierarchy != null)
            _viewer.SetGraphWithHierarchy(graph, hierarchy);
        else if (graph != null)
            _viewer.SetGraph(graph);

        if (selection.Length > 0)
            _viewer.SetSelection(selection);

        newCtrl.Focus();
        UpdateStatusFromGraph();
    }

    private void WireViewerEvents()
    {
        _viewer.LevelChanged += OnLevelChanged;
        _viewer.SelectionChanged += OnSelectionChanged;
        var ctrl = (Control)_viewer;
        ctrl.MouseClick += OnGraphClick;
        ctrl.MouseDoubleClick += OnGraphDoubleClick;
    }

    private void UnwireViewerEvents()
    {
        _viewer.LevelChanged -= OnLevelChanged;
        _viewer.SelectionChanged -= OnSelectionChanged;
        var ctrl = (Control)_viewer;
        ctrl.MouseClick -= OnGraphClick;
        ctrl.MouseDoubleClick -= OnGraphDoubleClick;
    }

    // ════════════════════════════════════════════════════════════════════
    // Public API (delegates to active viewer)
    // ════════════════════════════════════════════════════════════════════

    public void SetStatus(string text) => _statusLabel.Text = text;

    public void SetNodeColorMode(string mode) => _viewer.NodeColorMode = mode;
    public void SetEdgeColorMode(string mode) => _viewer.EdgeColorMode = mode;
    public string NodeColorMode => _viewer.NodeColorMode;
    public string EdgeColorMode => _viewer.EdgeColorMode;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLabels
    {
        get => _viewer.ShowLabels;
        set
        {
            _viewer.ShowLabels = value;
            _showLabelsItem.Checked = value;
            ViewerControl.Invalidate();
        }
    }

    private static void UpdateColorMenuChecks(ToolStripMenuItem parentMenu, string activeMode)
    {
        foreach (ToolStripMenuItem item in parentMenu.DropDownItems)
            item.Checked = (string)item.Tag! == activeMode;
    }

    /// <summary>Update status bar to reflect current graph/level/selection state.</summary>
    private void UpdateStatusFromGraph()
    {
        var graph = _viewer.GetGraph();
        if (graph == null) return;

        string viewerName = _viewer.ViewerType;
        string status = $"◇  {graph.Title}  —  {graph.NodeCount}n, {graph.EdgeCount}e  [{viewerName}]";

        var hierarchy = _viewer.GetHierarchy();
        if (hierarchy != null && hierarchy.LevelCount > 1)
        {
            status += $"  |  Level {_viewer.CurrentLevel}/{hierarchy.LevelCount - 1}";
            if (_viewer.CurrentLevel == 0) status += " (finest)";
            else if (_viewer.CurrentLevel == hierarchy.LevelCount - 1) status += " (coarsest)";
        }

        int selCount = _viewer.Selection.Count;
        if (selCount > 0)
            status += $"  |  {selCount} selected";

        _statusLabel.Text = status;
    }

    private void OnLevelChanged()
    {
        UpdateStatusFromGraph();
        EnqueueEvent(new JsonObject
        {
            ["type"] = "level_changed",
            ["level"] = _viewer.CurrentLevel,
            ["level_count"] = _viewer.LevelCount,
        });
    }

    private void OnSelectionChanged()
    {
        UpdateStatusFromGraph();
        EnqueueEvent(new JsonObject
        {
            ["type"] = "selection_changed",
            ["count"] = _viewer.Selection.Count,
        });
    }

    /// <summary>
    /// Drain all pending interaction events. Returns them and clears the queue.
    /// </summary>
    public JsonArray DrainInteractions()
    {
        var result = new JsonArray();
        while (_interactions.TryDequeue(out var ev))
            result.Add(ev);
        return result;
    }

    // ── Interaction tracking ────────────────────────────────────────────

    private void EnqueueEvent(JsonObject ev)
    {
        ev["window"] = _windowId;
        ev["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _interactions.Enqueue(ev);

        while (_interactions.Count > 200)
            _interactions.TryDequeue(out _);
    }

    private void OnGraphClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) return;

        EnqueueEvent(new JsonObject
        {
            ["type"] = "click",
            ["button"] = e.Button.ToString().ToLowerInvariant(),
            ["x"] = e.X,
            ["y"] = e.Y,
        });
    }

    private void OnGraphDoubleClick(object? sender, MouseEventArgs e)
    {
        EnqueueEvent(new JsonObject
        {
            ["type"] = "double_click",
            ["x"] = e.X,
            ["y"] = e.Y,
        });
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // 'V' key cycles viewer types
        if (e.KeyCode == Keys.V && e.Modifiers == Keys.None)
        {
            string next = _viewer.ViewerType == "force" ? "matrix" : "force";
            SetViewerType(next);
            e.Handled = true;
            return;
        }

        // 'L' toggles labels — sync menu check
        if (e.KeyCode == Keys.L && e.Modifiers == Keys.None)
        {
            _showLabelsItem.Checked = !_showLabelsItem.Checked;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Navigation keys handled by the viewer's OnKeyDown directly.
        if (e.KeyCode is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
            or Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Oemplus or Keys.OemMinus or Keys.Add or Keys.Subtract
            or Keys.F or Keys.M or Keys.E
            or Keys.D0 or Keys.D9)
            return;

        EnqueueEvent(new JsonObject
        {
            ["type"] = "key_down",
            ["key"] = e.KeyCode.ToString(),
            ["modifiers"] = e.Modifiers.ToString(),
        });
    }

    private void OnWindowResize(object? sender, EventArgs e)
    {
        EnqueueEvent(new JsonObject
        {
            ["type"] = "resize",
            ["width"] = ClientSize.Width,
            ["height"] = ClientSize.Height,
        });
    }

    private void OnWindowClosed(object? sender, FormClosedEventArgs e)
    {
        EnqueueEvent(new JsonObject
        {
            ["type"] = "window_closed",
        });
        WindowClosed?.Invoke(_windowId);
    }
}
