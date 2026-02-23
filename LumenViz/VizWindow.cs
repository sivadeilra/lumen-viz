using System.Collections.Concurrent;
using System.Text.Json.Nodes;

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

        // Wire level-change events so we update the status bar
        _graphView.LevelChanged += OnLevelChanged;
        _graphView.SelectionChanged += OnSelectionChanged;

        // ── Status bar ──────────────────────────────────────────────────
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel($"◇  {id}  —  Ready");
        _statusBar.Items.Add(_statusLabel);

        // ── Form setup ──────────────────────────────────────────────────
        Text = title;
        ClientSize = new Size(width, height);
        MainMenuStrip = _menuStrip;
        Controls.Add(_graphView);    // Dock.Fill — behind everything
        Controls.Add(_menuStrip);
        Controls.Add(_statusBar);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Wire up interaction tracking ────────────────────────────────
        _graphView.MouseClick += OnGraphClick;
        _graphView.MouseDoubleClick += OnGraphDoubleClick;
        KeyPreview = true;
        KeyDown += OnWindowKeyDown;
        Resize += OnWindowResize;
        FormClosed += OnWindowClosed;
    }

    public void SetStatus(string text) => _statusLabel.Text = text;

    /// <summary>Update status bar to reflect current graph/level/selection state.</summary>
    private void UpdateStatusFromGraph()
    {
        var graph = _graphView.GetGraph();
        if (graph == null) return;

        string status = $"◇  {graph.Title}  —  {graph.NodeCount}n, {graph.EdgeCount}e";

        var hierarchy = _graphView.GetHierarchy();
        if (hierarchy != null && hierarchy.LevelCount > 1)
        {
            int lvl = _graphView.CurrentLevel;
            status += $"  |  Level {lvl}/{hierarchy.LevelCount - 1}";
            if (lvl == 0) status += " (finest)";
            else if (lvl == hierarchy.LevelCount - 1) status += " (coarsest)";
        }

        if (_graphView.Selection.Count > 0)
            status += $"  |  {_graphView.Selection.Count} selected";

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
        // PgUp/PgDn/Home/End are handled by GraphView.OnKeyDown directly.
        // Don't double-report those as interaction events.
        if (e.KeyCode is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
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
