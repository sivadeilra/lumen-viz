using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// A managed visualization window. Created and controlled via MCP tools.
/// Each window has a unique ID, a GraphView, a status bar, and an
/// interaction event queue that the MCP client can poll.
/// </summary>
public class VizWindow : Form
{
    private readonly string _windowId;
    private readonly GraphView _graphView;
    private readonly SkiaGraphView _skiaView;
    private bool _useSkia;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly MenuStrip _menuStrip;

    /// <summary>
    /// Thread-safe queue of user interaction events (node clicks, key
    /// presses, window resize, etc.). Polled by MCP `get_interactions` tool.
    /// </summary>
    private readonly ConcurrentQueue<JsonObject> _interactions = new();

    /// <summary>Fired when the user closes this window.</summary>
    public event Action<string>? WindowClosed;

    public string WindowId => _windowId;
    public GraphView GraphView => _graphView;
    public SkiaGraphView SkiaView => _skiaView;
    public bool UseSkia => _useSkia;

    public VizWindow(string id, string title, int width, int height)
    {
        _windowId = id;

        // ── Menu strip ──────────────────────────────────────────────────
        _menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        exitItem.ShortcutKeys = Keys.Alt | Keys.F4;
        fileMenu.DropDownItems.Add(exitItem);
        _menuStrip.Items.Add(fileMenu);

        // ── Graph view (fills the window) ───────────────────────────────
        _graphView = new GraphView { Dock = DockStyle.Fill };
        _skiaView = new SkiaGraphView { Dock = DockStyle.Fill, Visible = false };

        // Wire level-change events so we update the status bar
        _graphView.LevelChanged += OnLevelChanged;
        _graphView.SelectionChanged += OnSelectionChanged;
        _skiaView.LevelChanged += OnLevelChanged;
        _skiaView.SelectionChanged += OnSelectionChanged;

        // ── Status bar ──────────────────────────────────────────────────
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel($"◇  {id}  —  Ready");
        _statusBar.Items.Add(_statusLabel);

        // ── Form setup ──────────────────────────────────────────────────
        Text = title;
        ClientSize = new Size(width, height);
        MainMenuStrip = _menuStrip;
        Controls.Add(_graphView);    // Dock.Fill — behind everything
        Controls.Add(_skiaView);     // Skia view (hidden initially)
        Controls.Add(_menuStrip);
        Controls.Add(_statusBar);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Wire up interaction tracking ────────────────────────────────
        _graphView.MouseClick += OnGraphClick;
        _graphView.MouseDoubleClick += OnGraphDoubleClick;
        _skiaView.MouseClick += OnGraphClick;
        _skiaView.MouseDoubleClick += OnGraphDoubleClick;
        KeyPreview = true;
        KeyDown += OnWindowKeyDown;
        Resize += OnWindowResize;
        FormClosed += OnWindowClosed;
    }

    public void SetStatus(string text) => _statusLabel.Text = text;

    /// <summary>Switch between GDI+ and Skia renderer. Transfers graph state.</summary>
    public void SetRenderer(bool useSkia)
    {
        if (_useSkia == useSkia) return;
        _useSkia = useSkia;

        // Transfer graph/hierarchy from old renderer to new.
        // IMPORTANT: Make the target view visible FIRST so AutoFit()
        // (called by SetGraph*) has correct Width/Height.
        if (useSkia)
        {
            var g = _graphView.GetGraph();
            var h = _graphView.GetHierarchy();

            _graphView.Visible = false;
            _skiaView.Visible = true;

            if (g != null && h != null)
                _skiaView.SetGraphWithHierarchy(g, h);
            else if (g != null)
                _skiaView.SetGraph(g);

            _skiaView.Focus();
        }
        else
        {
            var g = _skiaView.GetGraph();
            var h = _skiaView.GetHierarchy();

            _skiaView.Visible = false;
            _graphView.Visible = true;

            if (g != null && h != null)
                _graphView.SetGraphWithHierarchy(g, h);
            else if (g != null)
                _graphView.SetGraph(g);

            _graphView.Focus();
        }

        UpdateStatusFromGraph();
    }

    /// <summary>Update status bar to reflect current graph/level/selection state.</summary>
    private void UpdateStatusFromGraph()
    {
        GraphModel? graph;
        CoarseningHierarchy? hierarchy;
        int currentLevel, selCount;

        if (_useSkia)
        {
            graph = _skiaView.GetGraph();
            hierarchy = _skiaView.GetHierarchy();
            currentLevel = _skiaView.CurrentLevel;
            selCount = _skiaView.Selection.Count;
        }
        else
        {
            graph = _graphView.GetGraph();
            hierarchy = _graphView.GetHierarchy();
            currentLevel = _graphView.CurrentLevel;
            selCount = _graphView.Selection.Count;
        }

        if (graph == null) return;

        string renderer = _useSkia ? "Skia" : "GDI+";
        string status = $"◇  {graph.Title}  —  {graph.NodeCount}n, {graph.EdgeCount}e  [{renderer}]";

        if (hierarchy != null && hierarchy.LevelCount > 1)
        {
            status += $"  |  Level {currentLevel}/{hierarchy.LevelCount - 1}";
            if (currentLevel == 0) status += " (finest)";
            else if (currentLevel == hierarchy.LevelCount - 1) status += " (coarsest)";
        }

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
            ["level"] = _graphView.CurrentLevel,
            ["level_count"] = _graphView.LevelCount,
        });
    }

    private void OnSelectionChanged()
    {
        UpdateStatusFromGraph();
        EnqueueEvent(new JsonObject
        {
            ["type"] = "selection_changed",
            ["count"] = _graphView.Selection.Count,
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

        // Cap the queue at 200 events to prevent unbounded growth
        while (_interactions.Count > 200)
            _interactions.TryDequeue(out _);
    }

    private void OnGraphClick(object? sender, MouseEventArgs e)
    {
        // Selection is handled by GraphView itself now.
        // Only track interaction events for non-selection clicks (right/middle).
        if (e.Button == MouseButtons.Left) return;

        var ev = new JsonObject
        {
            ["type"] = "click",
            ["button"] = e.Button.ToString().ToLowerInvariant(),
            ["x"] = e.X,
            ["y"] = e.Y,
        };
        EnqueueEvent(ev);
    }

    private void OnGraphDoubleClick(object? sender, MouseEventArgs e)
    {
        var ev = new JsonObject
        {
            ["type"] = "double_click",
            ["x"] = e.X,
            ["y"] = e.Y,
        };
        EnqueueEvent(ev);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // 'S' key toggles between GDI+ and Skia renderer
        if (e.KeyCode == Keys.S && e.Modifiers == Keys.None)
        {
            SetRenderer(!_useSkia);
            e.Handled = true;
            return;
        }

        // Navigation keys handled by GraphView.OnKeyDown directly.
        // Don't double-report those as interaction events.
        if (e.KeyCode is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
            or Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Oemplus or Keys.OemMinus or Keys.Add or Keys.Subtract
            or Keys.F or Keys.L or Keys.M or Keys.E
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
