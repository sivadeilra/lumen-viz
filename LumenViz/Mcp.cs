using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// Windowless MCP (Model Context Protocol) server.
///
/// Runs as a headless process — no windows on startup. MCP tools create,
/// command, and query windows. User interactions are queued and polled.
///
/// JSON-RPC 2.0 over stdin/stdout.
/// </summary>
public class McpServer
{
    private readonly ApplicationContext _appContext;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();

    /// <summary>Windows managed by this server, keyed by ID.</summary>
    private readonly ConcurrentDictionary<string, VizWindow> _windows = new();
    private int _windowCounter;

    /// <summary>Headless graph store, independent of windows.</summary>
    private readonly ConcurrentDictionary<string, GraphModel> _graphs = new();
    private int _graphCounter;

    /// <summary>
    /// Hidden form used to marshal calls to the UI thread when no
    /// visible windows exist yet.
    /// </summary>
    private Form? _invokeHelper;

    public McpServer(ApplicationContext appContext, TextReader input, TextWriter output)
    {
        _appContext = appContext;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Start the MCP read loop on a background thread.
    /// Call this after Application.Run() has started the message pump.
    /// </summary>
    public void Start()
    {
        var thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "MCP Stdin Reader",
        };
        thread.Start();
    }

    /// <summary>
    /// Create the hidden invoke-helper form on the UI thread.
    /// Must be called from the UI thread (e.g. before Application.Run
    /// or via form.Load).
    /// </summary>
    public void CreateInvokeHelper()
    {
        _invokeHelper = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
            Size = new Size(1, 1),
        };
        _invokeHelper.Show();
        _invokeHelper.Hide();
    }

    // -----------------------------------------------------------------------
    // Read loop
    // -----------------------------------------------------------------------

    private void ReadLoop()
    {
        Log("MCP server started (windowless), reading stdin...");
        try
        {
            while (true)
            {
                var line = _input.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var msg = JsonNode.Parse(line);
                    if (msg != null)
                        HandleMessage(msg);
                }
                catch (Exception ex)
                {
                    Log($"Error handling message: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Read loop error: {ex.Message}");
        }

        Log("Stdin closed, shutting down.");
        InvokeOnUI(() =>
        {
            foreach (var w in _windows.Values)
                w.Close();
            _appContext.ExitThread();
        });
    }

    // -----------------------------------------------------------------------
    // Message dispatch
    // -----------------------------------------------------------------------

    private void HandleMessage(JsonNode msg)
    {
        var id = msg["id"];
        var method = msg["method"]?.GetValue<string>();

        Log($"<-- {method} (id={id})");

        switch (method)
        {
            case "initialize":
                SendResult(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["prompts"] = new JsonObject(),
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "lumen-viz",
                        ["version"] = "0.2.0",
                    },
                });
                break;

            case "notifications/initialized":
                break;

            case "tools/list":
                SendResult(id, new JsonObject
                {
                    ["tools"] = BuildToolList(),
                });
                break;

            case "tools/call":
                HandleToolCall(id, msg["params"]);
                break;

            case "prompts/list":
                SendResult(id, new JsonObject
                {
                    ["prompts"] = BuildPromptList(),
                });
                break;

            case "prompts/get":
                HandlePromptGet(id, msg["params"]);
                break;

            default:
                if (id != null)
                    SendError(id, -32601, $"Unknown method: {method}");
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Tool schema helpers
    // -----------------------------------------------------------------------

    private static JsonObject ToolDef(
        string name, string description, JsonObject inputSchema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
        };
    }

    private static JsonObject Props(
        params (string name, string type, string description, bool required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc, req) in properties)
        {
            props[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = desc,
            };
            if (req) required.Add(name);
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    /// <summary>Shorthand: all properties required.</summary>
    private static JsonObject PropsReq(
        params (string name, string type, string description)[] properties)
    {
        var tuples = properties.Select(p => (p.name, p.type, p.description, true)).ToArray();
        return Props(tuples);
    }

    // -----------------------------------------------------------------------
    // Tool registry
    // -----------------------------------------------------------------------

    private static JsonArray BuildToolList()
    {
        return new JsonArray
        {
            // ── Window management ──────────────────────────────────────
            ToolDef("create_window",
                "Create a new visualization window. Returns its window ID.",
                Props(
                    ("title", "string", "Window title", false),
                    ("width", "string", "Client width in pixels (default 1280)", false),
                    ("height", "string", "Client height in pixels (default 800)", false),
                    ("viewer_type", "string", "Viewer type: 'force' (default) or 'matrix'", false))),

            ToolDef("close_window",
                "Close a window by ID.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("list_windows",
                "List all open windows with their IDs and titles.",
                Props()),

            ToolDef("get_window_info",
                "Get info about a window (title, size, state, graph info).",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("set_window_title",
                "Set the title of a window.",
                PropsReq(("window", "string", "Window ID"),
                      ("title", "string", "New title"))),

            ToolDef("get_interactions",
                "Drain all pending user interaction events from a window (clicks, keys, resize). " +
                "Returns an array of event objects. Each event has a type, timestamp, and " +
                "event-specific fields (e.g. node_index for node clicks).",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("show_message",
                "Show a modal message box on a window.",
                Props(("window", "string", "Window ID", true),
                      ("text", "string", "Message body", true),
                      ("caption", "string", "Dialog title", false))),

            // ── Graph visualization ────────────────────────────────────
            ToolDef("load_graph",
                "Load a graph file into a window. Supports: .mtx (Matrix Market), " +
                ".gml (GML), .graphml (GraphML), .csv/.tsv/.edges/.txt (edge list). " +
                "Runs community detection and force-directed layout automatically.",
                PropsReq(("window", "string", "Window ID"),
                      ("path", "string", "Absolute path to a graph file"))),

            ToolDef("load_graph_with_layout",
                "Load a graph with custom layout parameters.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("path", "string", "Absolute path to a graph file (.mtx, .gml, .graphml, .csv, .edges, etc.)", true),
                    ("iterations", "string", "Layout iterations (default 300)", false),
                    ("gravity", "string", "Gravity constant (default 0.05)", false),
                    ("layout", "string", "Layout mode: 'auto' (default), 'flat', or 'multilevel'", false),
                    ("bounded", "string", "'true' for rectangle-bounded layout, 'false' (default) for unbounded free space", false),
                    ("aspect_ratio", "string", "Aspect ratio W:H for bounded mode, e.g. '4:3' or '16:9' (default '1:1')", false))),

            ToolDef("get_graph_info",
                "Get info about the graph loaded in a window.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("rerun_layout",
                "Re-run force-directed layout with fresh random positions.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("iterations", "string", "Number of iterations (default 300)", false),
                    ("layout", "string", "Layout mode: 'auto' (default), 'flat', or 'multilevel'", false),
                    ("bounded", "string", "'true' for rectangle-bounded, 'false' (default) for unbounded", false),
                    ("aspect_ratio", "string", "Aspect ratio W:H for bounded mode, e.g. '4:3' (default '1:1')", false))),

            ToolDef("auto_fit",
                "Re-center and zoom to fit the graph in a window.",
                PropsReq(("window", "string", "Window ID"))),

            // ── Visual settings ────────────────────────────────────────
            ToolDef("set_show_labels",
                "Toggle node label display in a window.",
                PropsReq(("window", "string", "Window ID"),
                      ("show", "string", "true or false"))),

            ToolDef("set_node_radius",
                "Set the base node radius (pixels).",
                PropsReq(("window", "string", "Window ID"),
                      ("radius", "string", "Radius (e.g. 4, 6, 10)"))),

            ToolDef("set_edge_alpha",
                "Set edge transparency (0.0 invisible \u2013 1.0 opaque).",
                PropsReq(("window", "string", "Window ID"),
                      ("alpha", "string", "Alpha value 0.0\u20131.0"))),

            ToolDef("set_label_text",
                "Set the status bar text in a window.",
                PropsReq(("window", "string", "Window ID"),
                      ("text", "string", "The text to display"))),

            ToolDef("set_label_color",
                "Set the status bar text color.",
                PropsReq(("window", "string", "Window ID"),
                      ("color", "string", "Named color or hex (#RRGGBB)"))),

            // ── Data ───────────────────────────────────────────────────
            ToolDef("list_data_files",
                "List available graph data files in c:\\lumen-viz\\data. " +
                "Supports: .mtx, .gml, .graphml, .csv, .tsv, .edges, .txt",
                Props()),

            // ── Window geometry ─────────────────────────────────────────
            ToolDef("move_window",
                "Move a window to a specific screen position (top-left corner).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("x", "string", "X position in pixels", true),
                    ("y", "string", "Y position in pixels", true))),

            ToolDef("resize_window",
                "Resize a window's client area.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("width", "string", "Client width in pixels", true),
                    ("height", "string", "Client height in pixels", true))),

            ToolDef("arrange_windows",
                "Tile all open windows in a grid on screen. " +
                "Automatically calculates positions based on the number of windows.",
                Props()),

            // ── Process ────────────────────────────────────────────────
            ToolDef("exit_process",
                "Shut down the MCP server process. Use this to stop the server " +
                "before rebuilding. The MCP client will need to restart it.",
                Props()),

            // ── Selection ──────────────────────────────────────────────
            ToolDef("get_selection",
                "Get the currently selected node indices and labels in a window. " +
                "Returns an array of {index, label, community} objects.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("get_selection_summary",
                "Quickly check which windows have any nodes selected. " +
                "Returns a list of window IDs with their selection counts.",
                Props()),

            ToolDef("clear_selection",
                "Clear the node selection in a window.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("set_selection",
                "Set the selected nodes by index. Replaces any existing selection.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("indices", "string", "Comma-separated node indices (e.g. '0,5,12,99')", true))),

            ToolDef("select_top_nodes",
                "Select the top N nodes by a graph metric. " +
                "Metrics: degree, indegree, outdegree, pagerank, betweenness, " +
                "clustering, kcore, in_out_ratio, reciprocity. " +
                "Use order=asc for bottom-N (lowest metric). " +
                "Returns the selected node labels and metric values.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("metric", "string", "Metric name (e.g. 'pagerank')", true),
                    ("count", "string", "Number of nodes to select (default 20)", false),
                    ("order", "string", "Sort order: desc (default, highest first) or asc", false),
                    ("community", "string", "Restrict to nodes in this community (integer)", false))),

            // ── Coarsening levels ──────────────────────────────────────
            ToolDef("get_coarse_levels",
                "Get info about all coarsening levels for a window's graph. " +
                "Returns node/edge counts per level. Level 0 = finest (original).",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("set_coarse_level",
                "Navigate to a specific coarsening level in a window. " +
                "Level 0 = finest (original graph), higher = coarser. " +
                "Triggers smooth animated transition.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("level", "string", "Target level (0 = finest)", true))),

            // ── Performance instrumentation ─────────────────────────────
            ToolDef("get_render_stats",
                "Get per-phase rendering performance stats (ms) for a window. " +
                "Returns last-frame and EMA average for each phase, plus current settings. " +
                "Trigger a repaint first if you want fresh numbers.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("reset_render_stats",
                "Reset the EMA counters so measurements start fresh. " +
                "Call before starting an experiment to get clean averages.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("set_render_option",
                "Set a boolean or string rendering option for experiments. " +
                "Options: anti_alias, show_edges, show_nodes, show_outlines, show_labels, " +
                "show_minimap, show_levels_panel, show_parent_highlight, " +
                "show_off_screen_indicators, show_selection_glow (all bool: true/false), " +
                "lod_mode (string: auto/low/high).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("option", "string", "Option name (e.g. show_edges)", true),
                    ("value", "string", "Option value (true/false or string)", true))),

            ToolDef("set_color_mode",
                "Set the node and/or edge color mode. " +
                "Node modes: community (categorical), degree (sequential gradient), " +
                "in_out_ratio (diverging, directed only), betweenness (centrality heatmap), " +
                "pagerank (iterative importance, directed only), " +
                "clustering (local clustering coefficient), kcore (k-core shell number). " +
                "Edge modes: uniform (gray), community (intra/inter colored), " +
                "weight (gradient by edge weight), reciprocity (mutual vs one-way, directed), " +
                "bridge (highlight bridge/cut edges).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("node_mode", "string",
                        "Node color mode: community, degree, in_out_ratio, betweenness, pagerank, clustering, kcore", false),
                    ("edge_mode", "string",
                        "Edge color mode: uniform, community, weight, reciprocity, bridge", false))),

            ToolDef("get_color_mode",
                "Get the current node and edge color mode for a window.",
                PropsReq(("window", "string", "Window ID"))),

            // ── Headless graph store ───────────────────────────────────
            ToolDef("graph_load",
                "Load a graph file into the headless graph store (no window needed). " +
                "Returns a graph_id for use with other graph_* tools. " +
                "Community detection is run automatically.",
                Props(
                    ("path", "string", "Path to graph file", true),
                    ("graph_id", "string", "Optional custom ID. Auto-generated if omitted.", false))),

            ToolDef("graph_list",
                "List all graphs currently held in the headless graph store.",
                new JsonObject()),

            ToolDef("graph_info",
                "Get info about a graph in the store (nodes, edges, directed, communities).",
                PropsReq(("graph", "string", "Graph ID from graph_load"))),

            ToolDef("graph_metrics",
                "Compute a metric for all nodes and return the top/bottom N results. " +
                "Available metrics: degree, indegree, outdegree, pagerank, betweenness, " +
                "clustering, kcore, in_out_ratio, reciprocity.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metric", "string", "Metric name", true),
                    ("count", "string", "Number of results (default 20)", false),
                    ("order", "string", "desc (default) or asc", false),
                    ("community", "string", "Filter to a specific community number", false))),

            ToolDef("graph_stats",
                "Compute descriptive statistics for a node metric: mean, median, std dev, " +
                "skewness, kurtosis, quantiles (5%, 25%, 75%, 95%), min, max.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metric", "string", "Metric name (degree, pagerank, betweenness, etc.)", true))),

            ToolDef("graph_histogram",
                "Compute a histogram of a node metric distribution.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metric", "string", "Metric name", true),
                    ("bins", "string", "Number of bins (default 20)", false))),

            ToolDef("graph_degree_distribution",
                "Get the degree distribution and test for power-law fit. " +
                "Returns (degree, count) pairs and power-law exponent with R².",
                PropsReq(("graph", "string", "Graph ID"))),

            ToolDef("graph_correlate",
                "Compute Pearson and Spearman correlation between two node metrics.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metric_a", "string", "First metric name", true),
                    ("metric_b", "string", "Second metric name", true))),

            ToolDef("graph_correlation_matrix",
                "Compute the full Pearson correlation matrix across all 9 node metrics. " +
                "Reveals which metrics are redundant vs independent.",
                PropsReq(("graph", "string", "Graph ID"))),

            ToolDef("graph_find_bridges",
                "Find 'power broker' nodes that rank in the top-N for multiple metrics simultaneously.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metrics", "string", "Comma-separated metric names (e.g. pagerank,betweenness)", true),
                    ("top_n", "string", "Top N per metric to consider (default 20)", false))),

            ToolDef("graph_neighbors",
                "Get the neighbors of a node with their labels, communities, and degrees.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("node", "string", "Node index", true))),

            ToolDef("graph_node_profile",
                "Get a full profile of a node: all 9 metrics, label, community, degree, neighbors count.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("node", "string", "Node index", true))),

            ToolDef("graph_fft",
                "Compute FFT power spectrum of a metric's sorted distribution. " +
                "Reveals periodic structure or characteristic scales in the network. " +
                "Returns dominant frequency peaks.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("metric", "string", "Metric name", true),
                    ("max_peaks", "string", "Max dominant peaks to return (default 10)", false))),

            ToolDef("graph_show",
                "Display a graph from the store in a visualization window. " +
                "Bridges headless analysis with visual exploration.",
                Props(
                    ("graph", "string", "Graph ID", true),
                    ("window", "string", "Window ID (auto-creates if needed)", true))),

            // ── Viewer type ────────────────────────────────────────────
            ToolDef("set_viewer_type",
                "Switch a window between viewer types: 'force' (force-directed layout) " +
                "or 'matrix' (adjacency matrix heatmap). Graph state is transferred " +
                "automatically. Press 'V' in a window to cycle viewer types.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("type", "string", "Viewer type: 'force' or 'matrix'", true))),

            // ── Matrix viewer settings ─────────────────────────────────
            ToolDef("set_matrix_ordering",
                "Set the node ordering mode for the adjacency matrix viewer. " +
                "Modes: 'community' (grouped by community, default), 'original' (file order), " +
                "'degree' (sorted by degree), 'bfs' (bandwidth-minimizing BFS from highest-degree node), " +
                "'spectral' (Fiedler vector ordering, minimizes bandwidth via graph Laplacian eigenvector), " +
                "or any metric name (e.g. 'pagerank', 'betweenness').",
                Props(
                    ("window", "string", "Window ID", true),
                    ("ordering", "string", "Ordering mode", true))),

            ToolDef("set_color_ramp",
                "Set the color ramp for the adjacency matrix heatmap. " +
                "Ramps: 'thermal' (black→blue→cyan→yellow→white, default), " +
                "'viridis' (perceptually uniform purple→green→yellow), " +
                "'grayscale' (black→white), 'cyan' (black→cyan).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("ramp", "string", "Color ramp name", true))),

            ToolDef("set_log_scale",
                "Toggle log-scale density in the adjacency matrix viewer. " +
                "Log scale makes sparse regions more visible (enabled by default).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("enabled", "string", "true or false", true))),

            ToolDef("set_gamma",
                "Set the gamma exponent for the adjacency matrix density mapping. " +
                "Values <1 lift midtones (better visibility of sparse regions), " +
                ">1 compresses them. Default 0.5. Range 0.1–5.0. " +
                "Common presets: 0.3 (very lifted), 0.5 (default), 0.7, 1.0 (linear), 1.5, 2.0 (compressed).",
                Props(
                    ("window", "string", "Window ID", true),
                    ("gamma", "string", "Gamma value (0.1–5.0)", true))),

            ToolDef("set_density_floor",
                "Set the density floor for the adjacency matrix. " +
                "Any nonzero density jumps to at least this fraction of the color ramp, " +
                "making even single-edge pixels clearly visible. " +
                "Default 0.3 (LUT index ~77). Range 0.0–0.9.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("floor", "string", "Floor value (0.0–0.9)", true))),

            // ── Graph partitioning (Mongoose-style multilevel) ────────
            ToolDef("graph_bisect",
                "Compute a balanced bisection of a graph using multilevel coarsening " +
                "with QP+FM refinement (Algorithm 1003, Davis et al. 2020). " +
                "Returns partition assignment, edge cut, sizes, and imbalance. " +
                "Works on the headless graph store or a window's graph.",
                Props(
                    ("graph", "string", "Graph name in headless store, or 'window:<id>' to use a window's graph", true),
                    ("target_split", "string", "Target fraction for partition A (default 0.5 = balanced)", false),
                    ("tolerance", "string", "Balance tolerance (default 0.25)", false),
                    ("matching", "string", "Matching strategy: 'random', 'hem', 'hemsr', 'hemsrdeg' (default)", false))),

            ToolDef("graph_partition",
                "Compute a k-way partition of a graph using recursive multilevel bisection. " +
                "Returns partition ID (0..k-1) for each vertex, plus summary statistics.",
                Props(
                    ("graph", "string", "Graph name in headless store, or 'window:<id>' to use a window's graph", true),
                    ("k", "string", "Number of partitions (default 2)", false),
                    ("target_split", "string", "Target fraction for bisection balance (default 0.5)", false),
                    ("tolerance", "string", "Balance tolerance (default 0.25)", false),
                    ("matching", "string", "Matching strategy: 'random', 'hem', 'hemsr', 'hemsrdeg' (default)", false))),
        };
    }

    // -----------------------------------------------------------------------
    // Prompt definitions
    // -----------------------------------------------------------------------

    private static JsonArray BuildPromptList()
    {
        return new JsonArray
        {
            PromptDef("getting-started",
                "Essential first-use workflow for LumenViz. Covers discovering data files, " +
                "creating windows, loading graphs, and basic visual tuning. Start here.",
                ("graph_file", "string", false,
                    "Optional path to a graph file. If omitted, prompt will suggest using list_data_files.")),

            PromptDef("analyze-network",
                "Step-by-step pattern for investigating a graph's structure: compute metrics, " +
                "find important nodes, cross-reference rankings, and highlight discoveries.",
                ("metric", "string", false,
                    "Primary metric to analyze (pagerank, betweenness, degree, kcore, clustering). Default: pagerank.")),

            PromptDef("color-modes-guide",
                "Reference for all 7 node color modes and 5 edge color modes. Explains what each " +
                "mode reveals and which combinations work best for different graph types."),

            PromptDef("performance-tuning",
                "Guide for optimizing rendering performance on large graphs. Covers renderer " +
                "selection (Skia vs GDI+), LOD modes, render stats, and visual toggles."),

            PromptDef("multi-level-exploration",
                "How to use the hierarchical coarsening system to explore graphs at multiple " +
                "scales. Navigate between coarse overviews and full-detail views."),
        };
    }

    private static JsonObject PromptDef(string name, string description,
        params (string name, string type, bool required, string description)[] arguments)
    {
        var prompt = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
        };
        if (arguments.Length > 0)
        {
            var args = new JsonArray();
            foreach (var (aName, aType, aRequired, aDesc) in arguments)
            {
                args.Add(new JsonObject
                {
                    ["name"] = aName,
                    ["description"] = aDesc,
                    ["required"] = aRequired,
                });
            }
            prompt["arguments"] = args;
        }
        return prompt;
    }

    // -----------------------------------------------------------------------
    // Prompt dispatch
    // -----------------------------------------------------------------------

    private void HandlePromptGet(JsonNode? id, JsonNode? parameters)
    {
        var promptName = parameters?["name"]?.GetValue<string>();
        if (promptName == null)
        {
            SendError(id, -32602, "Missing prompt name");
            return;
        }

        var args = parameters?["arguments"];
        string? content = promptName switch
        {
            "getting-started" => PromptGettingStarted(args),
            "analyze-network" => PromptAnalyzeNetwork(args),
            "color-modes-guide" => PromptColorModesGuide(),
            "performance-tuning" => PromptPerformanceTuning(),
            "multi-level-exploration" => PromptMultiLevelExploration(),
            _ => null,
        };

        if (content == null)
        {
            SendError(id, -32602, $"Unknown prompt: {promptName}");
            return;
        }

        SendResult(id, new JsonObject
        {
            ["description"] = $"Prompt: {promptName}",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = content,
                    },
                },
            },
        });
    }

    // -----------------------------------------------------------------------
    // Prompt content
    // -----------------------------------------------------------------------

    private static string PromptGettingStarted(JsonNode? args)
    {
        var file = args?["graph_file"]?.GetValue<string>();
        var fileHint = file != null
            ? $"Load the graph file: {file}"
            : "First, call `list_data_files` to see available graphs. Pick one to load.";

        return $"""
            You are controlling LumenViz, a graph visualization application, via MCP tools.

            ## Quick-start workflow

            1. **Discover data**: {fileHint}
               Available formats: edge list (.txt), GML (.gml), GraphML (.graphml), Matrix Market (.mtx).

            2. **Create a window and load a graph**:
               - Call `load_graph` with `window` (any ID like "win1") and `path` (full file path).
               - The window auto-creates if it doesn't exist. No need to call `create_window` separately.
               - The graph will be laid out automatically (force-directed with Barnes-Hut optimization).

            3. **Adjust the view**:
               - `auto_fit` — zoom to fit all nodes in the viewport.
               - `set_edge_alpha` — lower to 0.05–0.15 for dense graphs so structure is visible.
               - `set_node_radius` — default is good for most graphs; increase for small graphs (<100 nodes).
               - `set_show_labels` — turn on for small graphs, off for large ones.

            4. **Explore the graph**:
               - `get_graph_info` — node/edge counts, community count, directed/undirected.
               - `set_color_mode` — try `node_mode=community` to see cluster structure.
               - `select_top_nodes` — highlight important nodes by any metric.
               - `get_coarse_levels` — see if multi-level hierarchy is available for zoom.

            ## Tips
            - Window IDs are arbitrary strings. Use "win1", "win2", etc.
            - Most tools return JSON with detailed results — parse them for follow-up decisions.
            - For graphs >1000 nodes, reduce edge alpha to 0.05–0.1 and consider `set_render_option` with `lod_mode=auto`.
            - The `load_graph_with_layout` tool gives fine control over layout: iterations, gravity, bounded mode, aspect ratio.
            - Use `arrange_windows` to tile multiple windows across the screen.
            """;
    }

    private static string PromptAnalyzeNetwork(JsonNode? args)
    {
        var metric = args?["metric"]?.GetValue<string>() ?? "pagerank";
        return $"""
            You are performing network analysis on a graph loaded in LumenViz.

            ## Analysis workflow

            ### Step 1: Understand the graph
            Call `get_graph_info` to learn:
            - Node and edge counts
            - Whether the graph is directed (affects which metrics are meaningful)
            - Number of communities detected

            ### Step 2: Rank nodes by metrics
            Use `select_top_nodes` to find important nodes. Start with `metric={metric}`.

            Available metrics (and what they reveal):
            - **pagerank** — Global influence/authority. Best first metric for directed graphs.
            - **betweenness** — Bridge nodes that control information flow between communities.
            - **degree** — Simple connectivity. High degree = hub nodes.
            - **kcore** — Coreness: how deeply embedded in the dense core of the network.
            - **clustering** — Local clustering coefficient. High = tightly-knit neighborhoods.
            - **in_out_ratio** — (Directed only) Ratio of in-degree to total degree. High = authority, Low = hub.
            - **indegree** / **outdegree** — (Directed only) Incoming vs outgoing connections.
            - **reciprocity** — (Directed only) Fraction of edges that are reciprocated.

            ### Step 3: Cross-reference metrics to find "power brokers"
            The most interesting nodes often rank high on MULTIPLE metrics simultaneously.
            For example, nodes that rank in both top-20 PageRank AND top-20 Betweenness are
            "power brokers" — they are both influential and structurally critical.

            Strategy:
            1. `select_top_nodes` with metric=pagerank, count=20 → note the node indices
            2. `select_top_nodes` with metric=betweenness, count=20 → note the node indices
            3. Find the intersection — nodes appearing in both lists
            4. `set_selection` with just those overlapping indices to highlight them
            5. Use `set_color_mode` with node_mode=community to see which communities they belong to

            ### Step 4: Visualize with color modes
            - `set_color_mode node_mode=pagerank` — color gradient shows influence distribution
            - `set_color_mode node_mode=betweenness` — highlights bridge nodes
            - `set_color_mode edge_mode=bridge` — colors inter-community edges differently

            ### Step 5: Community-scoped analysis
            Use the `community` parameter of `select_top_nodes` to find leaders within a specific community:
            `select_top_nodes metric=pagerank count=5 community=3`

            ## Tips for directed graphs
            - in_out_ratio is uniquely informative: values near 1.0 = pure authority, near 0.0 = pure hub
            - reciprocity reveals mutual relationships vs one-way flows
            - Use edge_mode=reciprocity to visualize this
            """;
    }

    private static string PromptColorModesGuide()
    {
        return """
            ## LumenViz Color Mode Reference

            Color modes are set with `set_color_mode` and queried with `get_color_mode`.
            You can set node_mode and edge_mode independently. Changes animate smoothly.

            ### Node Color Modes (7 available)

            | Mode | What it shows | Best for |
            |------|--------------|----------|
            | `community` | Louvain community membership. Each community gets a distinct hue. | Default first view. See cluster structure. |
            | `degree` | Total connections (in+out). Gradient: dark blue (low) → bright yellow (high). | Finding hubs in any graph. |
            | `in_out_ratio` | Ratio of in-degree to total degree. Blue=hub (mostly out), Red=authority (mostly in). | Directed graphs only. Reveals information flow direction. |
            | `betweenness` | Betweenness centrality. Hot gradient: cool (low) → red (high). | Finding bridges and gatekeepers between communities. |
            | `pagerank` | PageRank score. Purple (low) → orange/yellow (high). | Identifying globally important nodes in directed graphs. |
            | `clustering` | Local clustering coefficient. Low (dark) → high (bright green). | Finding tightly-knit cliques vs loosely connected nodes. |
            | `kcore` | K-core number. Deeper core = brighter. | Seeing the "onion layers" of network density. |

            ### Edge Color Modes (5 available)

            | Mode | What it shows | Best for |
            |------|--------------|----------|
            | `uniform` | All edges same semi-transparent color. | Clean, uncluttered view. Default. |
            | `community` | Same color as source node's community. Inter-community edges are gray. | Seeing community boundaries. |
            | `weight` | Edge weight gradient (if weighted). | Weighted networks. |
            | `reciprocity` | Reciprocated edges (both A→B and B→A exist) highlighted. | Directed graphs: mutual vs one-way relationships. |
            | `bridge` | Edges crossing community boundaries highlighted in distinct color. | Finding inter-community connections. |

            ### Recommended combinations

            - **First look**: node=community, edge=uniform — see the community structure clearly
            - **Hub analysis**: node=degree, edge=community — find hubs and their community context
            - **Bridge detection**: node=betweenness, edge=bridge — see gatekeepers and their cross-community edges
            - **Directed flow**: node=in_out_ratio, edge=reciprocity — understand information flow patterns
            - **Influence map**: node=pagerank, edge=community — see where power concentrates
            - **Dense core**: node=kcore, edge=uniform — reveal the hierarchical core structure

            ### Notes
            - Color transitions animate over ~330ms (smoothstep interpolation).
            - Modes that depend on direction (in_out_ratio, reciprocity) degrade gracefully on undirected graphs but are less meaningful.
            - betweenness is O(n*m) — may take a moment on large graphs (>5000 nodes).
            """;
    }

    private static string PromptPerformanceTuning()
    {
        return """
            ## LumenViz Performance Tuning Guide

            ### Monitoring performance
            - `get_render_stats` — returns frame time (EMA), node/edge counts rendered, LOD state.
            - `reset_render_stats` — clear accumulated stats for a fresh measurement window.
            - Check `get_window_info` for render stats embedded in the window info response.

            ### Renderer selection
            LumenViz has two renderers:
            - **GDI+** — Windows native. Good compatibility. Adequate for <2000 nodes.
            - **Skia** — GPU-accelerated via SkiaSharp. ~3× faster. Preferred for large graphs.

            Switch with: `set_render_option option=renderer value=skia` (or `gdi`).

            ### LOD (Level of Detail) modes
            `set_render_option option=lod_mode value=<mode>`
            - **auto** — Automatically reduces detail during interaction (pan/zoom). Best default.
            - **low** — Always low detail. Fastest for huge graphs.
            - **high** — Always full detail. Use for screenshots or small graphs.

            ### Visual toggles that affect performance
            These can be toggled via `set_render_option`:
            | Option | Default | Performance impact |
            |--------|---------|-------------------|
            | `show_edges` | true | **Major** — edges dominate render time on dense graphs. |
            | `show_labels` | varies | Moderate — text rendering is expensive for many nodes. |
            | `show_minimap` | true | Minor — renders a scaled-down copy. |
            | `show_outlines` | true | Minor — additional stroke pass per node. |
            | `anti_alias` | true | Moderate — smoother but slower. |
            | `show_selection_glow` | true | Minor — gaussian glow on selected nodes. |

            ### Quick recipe for large graphs (>5000 nodes)
            1. Use Skia renderer: `set_render_option option=renderer value=skia`
            2. Lower edge alpha: `set_edge_alpha alpha=0.05`
            3. Use auto LOD: `set_render_option option=lod_mode value=auto`
            4. Consider hiding labels: `set_render_option option=show_labels value=false`
            5. If still slow, hide edges during exploration: `set_render_option option=show_edges value=false`

            ### Multi-level as a performance strategy
            For very large graphs, the coarsening hierarchy gives you a natural performance escape:
            - `set_coarse_level level=2` — view a coarser representation with fewer nodes.
            - Navigate at the coarse level, then drill down to level 0 for the area of interest.
            """;
    }

    private static string PromptMultiLevelExploration()
    {
        return """
            ## Multi-Level Graph Exploration in LumenViz

            LumenViz uses Hierarchical Edge Merging (HEM) to build a coarsening hierarchy
            when a graph is loaded. This creates multiple levels of abstraction:
            - **Level 0** — the original full-detail graph.
            - **Level 1, 2, ...** — progressively coarser views where groups of nodes are merged.

            ### Checking available levels
            `get_coarse_levels` returns:
            - Number of levels and node/edge counts at each level.
            - Memory usage per level.
            - Which level is currently displayed.

            Graphs with >200 nodes automatically get multi-level layout ("auto" mode).
            Use `load_graph_with_layout layout=multilevel` to force it on smaller graphs.

            ### Navigating levels
            `set_coarse_level level=N`
            - Level 0 = full detail, higher levels = coarser.
            - Smooth animated transition between levels.
            - Each level preserves community structure (colors stay meaningful).

            ### Exploration strategy
            1. **Start coarse**: Set a high level to see the overall community structure.
               Fewer nodes means faster rendering and clearer big-picture patterns.
            2. **Identify regions of interest**: Use color modes (community, pagerank) at the coarse level.
            3. **Drill down**: Step down levels one at a time toward level 0.
               Each step reveals finer structure within the communities.
            4. **Use selection at fine level**: Once at level 0, use `select_top_nodes` to find
               specific important nodes within the regions you identified.

            ### Layout options
            When loading with `load_graph_with_layout`:
            - `iterations` — More iterations = better layout quality but slower. Default 300.
            - `gravity` — Pull toward center. Higher = more compact. Default 0.05.
            - `bounded` — Constrain layout to a bounding box (true/false).
            - `aspect_ratio` — e.g., "16:9" for wide layouts, "1:1" for square.
            - `layout` — "auto", "multilevel", or "flat" (no hierarchy).

            ### Tips
            - The levels panel in the UI shows all levels visually. Toggle with `set_render_option option=show_levels_panel`.
            - Parent highlight shows which coarse node contains each fine node. Auto-disables above 2000 nodes.
            - Re-run layout at any time with `rerun_layout` to improve node positioning.
            """;
    }

    // -----------------------------------------------------------------------
    // Tool dispatch
    // -----------------------------------------------------------------------

    private void HandleToolCall(JsonNode? id, JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        var args = parameters?["arguments"];

        if (toolName == null)
        {
            SendError(id, -32602, "Missing tool name");
            return;
        }

        Log($"    tool={toolName}");

        try
        {
            string result = toolName switch
            {
                // ── Window management ──────────────────────────────────
                "create_window" => CreateWindow(args),
                "close_window" => CloseWindow(args),
                "list_windows" => ListWindows(),
                "get_window_info" => GetWindowInfo(args),
                "set_window_title" => SetWindowTitle(args),
                "get_interactions" => GetInteractions(args),
                "show_message" => ShowMessage(args),

                // ── Graph visualization ────────────────────────────────
                "load_graph" => LoadGraph(args, 300, 0.05, "auto", false, "1:1"),
                "load_graph_with_layout" => LoadGraph(args,
                    IntArg(args, "iterations", 300),
                    DoubleArg(args, "gravity", 0.05),
                    ArgOr(args, "layout", "auto"),
                    BoolArg(args, "bounded", false),
                    ArgOr(args, "aspect_ratio", "1:1")),
                "get_graph_info" => GetGraphInfo(args),
                "rerun_layout" => RerunLayout(args, IntArg(args, "iterations", 300),
                    ArgOr(args, "layout", "auto"),
                    BoolArg(args, "bounded", false),
                    ArgOr(args, "aspect_ratio", "1:1")),
                "auto_fit" => AutoFitWindow(args),

                // ── Visual settings ────────────────────────────────────
                "set_show_labels" => SetShowLabels(args),
                "set_node_radius" => SetNodeRadius(args),
                "set_edge_alpha" => SetEdgeAlpha(args),
                "set_label_text" => SetLabelText(args),
                "set_label_color" => SetLabelColor(args),

                // ── Data ───────────────────────────────────────────────
                "list_data_files" => ListDataFiles(),

                // ── Window geometry ─────────────────────────────────────
                "move_window" => MoveWindow(args),
                "resize_window" => ResizeWindow(args),
                "arrange_windows" => ArrangeWindows(),

                // ── Process ────────────────────────────────────────────
                "exit_process" => ExitProcess(),

                // ── Selection ──────────────────────────────────────────
                "get_selection" => GetSelection(args),
                "get_selection_summary" => GetSelectionSummary(),
                "clear_selection" => ClearSelectionTool(args),
                "set_selection" => SetSelectionTool(args),
                "select_top_nodes" => SelectTopNodes(args),

                // ── Coarsening levels ──────────────────────────────────
                "get_coarse_levels" => GetCoarseLevels(args),
                "set_coarse_level" => SetCoarseLevel(args),

                // ── Performance instrumentation ───────────────────────────
                "get_render_stats" => GetRenderStats(args),
                "reset_render_stats" => ResetRenderStats(args),
                "set_render_option" => SetRenderOption(args),

                // ── Color modes ───────────────────────────────────────────
                "set_color_mode" => SetColorMode(args),
                "get_color_mode" => GetColorMode(args),

                // ── Headless graph store ──────────────────────────────────
                "graph_load" => GraphLoad(args),
                "graph_list" => GraphList(),
                "graph_info" => GraphInfo(args),
                "graph_metrics" => GraphMetrics(args),
                "graph_stats" => GraphStats(args),
                "graph_histogram" => GraphHistogram(args),
                "graph_degree_distribution" => GraphDegreeDistribution(args),
                "graph_correlate" => GraphCorrelate(args),
                "graph_correlation_matrix" => GraphCorrelationMatrix(args),
                "graph_find_bridges" => GraphFindBridges(args),
                "graph_neighbors" => GraphNeighbors(args),
                "graph_node_profile" => GraphNodeProfile(args),
                "graph_fft" => GraphFft(args),
                "graph_show" => GraphShow(args),

                // ── Viewer type ──────────────────────────────────────────
                "set_viewer_type" => SetViewerType(args),

                // ── Matrix viewer settings ───────────────────────────────
                "set_matrix_ordering" => SetMatrixOrdering(args),
                "set_color_ramp" => SetColorRamp(args),
                "set_log_scale" => SetLogScale(args),
                "set_gamma" => SetGamma(args),
                "set_density_floor" => SetDensityFloor(args),

                // ── Graph partitioning ────────────────────────────────────
                "graph_bisect" => GraphBisect(args),
                "graph_partition" => GraphPartition(args),

                _ => throw new InvalidOperationException(
                    $"Unknown tool: {toolName}"),
            };

            SendToolResult(id, result);
        }
        catch (Exception ex)
        {
            SendToolError(id, ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Window management tools
    // -----------------------------------------------------------------------

    private string CreateWindow(JsonNode? args)
    {
        string title = ArgOr(args, "title", "Lumen Viz  ◇");
        int width = IntArg(args, "width", 1280);
        int height = IntArg(args, "height", 800);
        string viewerType = ArgOr(args, "viewer_type", "force");
        int num = Interlocked.Increment(ref _windowCounter);
        string winId = $"win{num}";

        InvokeOnUI(() =>
        {
            var window = new VizWindow(winId, title, width, height, viewerType);
            window.WindowClosed += OnWindowClosed;
            _windows[winId] = window;
            window.Show();
        });

        Log($"    Created window {winId} ({width}x{height})");
        return new JsonObject
        {
            ["window"] = winId,
            ["title"] = title,
            ["width"] = width,
            ["height"] = height,
        }.ToJsonString();
    }

    private string CloseWindow(JsonNode? args)
    {
        var winId = Arg(args, "window");
        return InvokeOnUI(() =>
        {
            if (_windows.TryGetValue(winId, out var win))
            {
                win.Close();
                return "OK";
            }
            return $"Window not found: {winId}";
        });
    }

    private string ListWindows()
    {
        return InvokeOnUI(() =>
        {
            var list = new JsonArray();
            foreach (var (id, win) in _windows)
            {
                list.Add(new JsonObject
                {
                    ["id"] = id,
                    ["title"] = win.Text,
                    ["width"] = win.ClientSize.Width,
                    ["height"] = win.ClientSize.Height,
                    ["has_graph"] = win.Viewer.GetGraph() != null,
                    ["selected"] = win.Viewer.Selection.Count,
                });
            }
            return new JsonObject { ["windows"] = list }.ToJsonString();
        });
    }

    private string GetWindowInfo(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.Viewer.GetGraph();
            var info = new JsonObject
            {
                ["id"] = win.WindowId,
                ["title"] = win.Text,
                ["width"] = win.ClientSize.Width,
                ["height"] = win.ClientSize.Height,
                ["windowState"] = win.WindowState.ToString(),
                ["viewer_type"] = win.Viewer.ViewerType,
            };
            if (graph != null)
            {
                info["graph_title"] = graph.Title;
                info["graph_nodes"] = graph.NodeCount;
                info["graph_edges"] = graph.EdgeCount;
                info["graph_communities"] = CountCommunities(graph);
                info["selected_nodes"] = win.Viewer.Selection.Count;

                var hierarchy = win.Viewer.GetHierarchy();
                if (hierarchy != null)
                {
                    info["coarsening_levels"] = hierarchy.LevelCount;
                    info["current_level"] = win.Viewer.CurrentLevel;
                }
            }

            // Include render performance stats
            var stats = win.Viewer.GetRenderStats();
            info["render_stats"] = JsonSerializer.SerializeToNode(stats);

            return info.ToJsonString();
        });
    }

    private string SetWindowTitle(JsonNode? args)
    {
        var win = GetWindow(args);
        var title = Arg(args, "title");
        InvokeOnUI(() => { win.Text = title; });
        return "OK";
    }

    private string GetInteractions(JsonNode? args)
    {
        var win = GetWindow(args);
        var events = win.DrainInteractions();
        return new JsonObject
        {
            ["window"] = win.WindowId,
            ["count"] = events.Count,
            ["events"] = events,
        }.ToJsonString();
    }

    private string ShowMessage(JsonNode? args)
    {
        var win = GetWindow(args);
        var text = Arg(args, "text");
        var caption = ArgOr(args, "caption", "Lumen Viz");
        return InvokeOnUI(() =>
        {
            MessageBox.Show(win, text, caption,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return "OK";
        });
    }

    private void OnWindowClosed(string windowId)
    {
        _windows.TryRemove(windowId, out _);
        Log($"    Window {windowId} closed by user");
    }

    // -----------------------------------------------------------------------
    // Window geometry tools
    // -----------------------------------------------------------------------

    private string MoveWindow(JsonNode? args)
    {
        var win = GetWindow(args);
        int x = int.Parse(Arg(args, "x"));
        int y = int.Parse(Arg(args, "y"));
        InvokeOnUI(() =>
        {
            win.StartPosition = FormStartPosition.Manual;
            win.Location = new Point(x, y);
        });
        return "OK";
    }

    private string ResizeWindow(JsonNode? args)
    {
        var win = GetWindow(args);
        int w = int.Parse(Arg(args, "width"));
        int h = int.Parse(Arg(args, "height"));
        InvokeOnUI(() =>
        {
            win.ClientSize = new Size(w, h);
            win.Viewer.AutoFit();
            win.ViewerControl.Invalidate();
        });
        return "OK";
    }

    private string ArrangeWindows()
    {
        var ids = _windows.Keys.ToList();
        if (ids.Count == 0) return "No windows open";

        return InvokeOnUI(() =>
        {
            // Get primary screen working area
            var screen = Screen.PrimaryScreen?.WorkingArea
                ?? new Rectangle(0, 0, 1920, 1080);

            int count = ids.Count;
            // Calculate grid dimensions
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling((double)count / cols);

            int cellW = screen.Width / cols;
            int cellH = screen.Height / rows;

            for (int i = 0; i < count; i++)
            {
                if (!_windows.TryGetValue(ids[i], out var win)) continue;
                int col = i % cols;
                int row = i / cols;

                win.StartPosition = FormStartPosition.Manual;
                win.Location = new Point(
                    screen.Left + col * cellW,
                    screen.Top + row * cellH);
                win.ClientSize = new Size(cellW, cellH);
                win.Viewer.AutoFit();
                win.ViewerControl.Invalidate();
            }

            return $"Arranged {count} windows in {cols}x{rows} grid";
        });
    }

    // -----------------------------------------------------------------------
    // Selection tools
    // -----------------------------------------------------------------------

    private string GetSelection(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.Viewer.GetGraph();
            if (graph == null) return "No graph loaded";

            var selection = win.Viewer.Selection;
            var nodes = new JsonArray();
            foreach (int i in selection)
            {
                if (i >= graph.NodeCount) continue;
                nodes.Add(new JsonObject
                {
                    ["index"] = i,
                    ["label"] = graph.Labels[i],
                    ["community"] = graph.Community[i],
                });
            }
            return new JsonObject
            {
                ["window"] = win.WindowId,
                ["count"] = nodes.Count,
                ["nodes"] = nodes,
            }.ToJsonString();
        });
    }

    private string GetSelectionSummary()
    {
        return InvokeOnUI(() =>
        {
            var list = new JsonArray();
            foreach (var (id, win) in _windows)
            {
                int count = win.Viewer.Selection.Count;
                list.Add(new JsonObject
                {
                    ["window"] = id,
                    ["selected"] = count,
                    ["has_selection"] = count > 0,
                });
            }
            return new JsonObject { ["windows"] = list }.ToJsonString();
        });
    }

    private string ClearSelectionTool(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            win.Viewer.ClearSelection();
            return $"Selection cleared in {win.WindowId}";
        });
    }

    private string SetSelectionTool(JsonNode? args)
    {
        var win = GetWindow(args);
        string indicesStr = Arg(args, "indices");
        var indices = indicesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out int v) ? v : -1)
            .Where(v => v >= 0)
            .ToArray();

        return InvokeOnUI(() =>
        {
            win.Viewer.SetSelection(indices);
            return $"Selected {win.Viewer.Selection.Count} nodes in {win.WindowId}";
        });
    }

    private string SelectTopNodes(JsonNode? args)
    {
        var win = GetWindow(args);
        string metric = Arg(args, "metric");
        int count = int.TryParse(OptArg(args, "count"), out int c) ? c : 20;
        string order = OptArg(args, "order") ?? "desc";
        int? community = int.TryParse(OptArg(args, "community"), out int comm) ? comm : null;

        return InvokeOnUI(() =>
        {
            var graph = win.Viewer.GetGraph();
            if (graph == null) return "No graph loaded";

            var provider = new LumenGraph.NodeColorProvider();
            provider.SetGraph(graph);
            double[] values = provider.ComputeNodeMetric(metric);

            // Build candidate list (optionally filtered by community)
            var candidates = Enumerable.Range(0, graph.NodeCount);
            if (community.HasValue)
                candidates = candidates.Where(i => graph.Community[i] == community.Value);

            // Sort and take top N
            int[] sorted;
            if (order == "asc")
                sorted = candidates.OrderBy(i => values[i]).Take(count).ToArray();
            else
                sorted = candidates.OrderByDescending(i => values[i]).Take(count).ToArray();

            win.Viewer.SetSelection(sorted);

            // Build response with node details
            var nodes = new JsonArray();
            foreach (int i in sorted)
            {
                nodes.Add(new JsonObject
                {
                    ["index"] = i,
                    ["label"] = graph.Labels[i],
                    ["community"] = graph.Community[i],
                    [metric] = Math.Round(values[i], 6),
                });
            }
            return new JsonObject
            {
                ["window"] = win.WindowId,
                ["metric"] = metric,
                ["order"] = order,
                ["count"] = sorted.Length,
                ["nodes"] = nodes,
            }.ToJsonString();
        });
    }

    // -----------------------------------------------------------------------
    // Coarsening level tools
    // -----------------------------------------------------------------------

    private string GetCoarseLevels(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var hierarchy = win.Viewer.GetHierarchy();
            if (hierarchy == null) return "No coarsening hierarchy available";

            var levels = new JsonArray();
            long totalMemory = 0;
            for (int i = 0; i < hierarchy.LevelCount; i++)
            {
                var g = hierarchy.Graphs[i];
                long mem = g.EstimateMemoryBytes();
                totalMemory += mem;
                levels.Add(new JsonObject
                {
                    ["level"] = i,
                    ["nodes"] = g.NodeCount,
                    ["edges"] = g.EdgeCount,
                    ["memory_bytes"] = mem,
                    ["is_current"] = (i == win.Viewer.CurrentLevel),
                });
            }
            return new JsonObject
            {
                ["window"] = win.WindowId,
                ["current_level"] = win.Viewer.CurrentLevel,
                ["level_count"] = hierarchy.LevelCount,
                ["total_memory_bytes"] = totalMemory,
                ["levels"] = levels,
            }.ToJsonString();
        });
    }

    private string SetCoarseLevel(JsonNode? args)
    {
        var win = GetWindow(args);
        int level = int.Parse(Arg(args, "level"));
        return InvokeOnUI(() =>
        {
            if (win.Viewer.SetLevel(level))
                return $"Navigating to level {level}";
            return $"Cannot navigate to level {level} — out of range or no hierarchy";
        });
    }

    // -----------------------------------------------------------------------
    // Performance instrumentation tools
    // -----------------------------------------------------------------------

    private string GetRenderStats(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var stats = win.Viewer.GetRenderStats();
            return System.Text.Json.JsonSerializer.Serialize(stats,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });
    }

    private string ResetRenderStats(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            win.Viewer.ResetRenderStats();
            win.ViewerControl.Invalidate();
            return "Render stats reset. Next paint will start fresh EMA.";
        });
    }

    private string SetRenderOption(JsonNode? args)
    {
        var win = GetWindow(args);
        string option = Arg(args, "option");
        string value = Arg(args, "value");

        return InvokeOnUI(() =>
        {
            // Handle viewer type switch (replaces the old renderer toggle)
            if (option == "viewer_type")
            {
                if (value is not ("force" or "matrix"))
                    return $"Invalid viewer_type: {value}. Use 'force' or 'matrix'.";
                win.SetViewerType(value);
                return $"Switched to {value} viewer.";
            }

            var viewer = win.Viewer;
            bool boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

            // Interface-level settings (all viewer types)
            switch (option)
            {
                case "show_edges": viewer.ShowEdges = boolVal; break;
                case "show_nodes": viewer.ShowNodes = boolVal; break;
                case "show_labels": viewer.ShowLabels = boolVal; break;
                case "show_minimap": viewer.ShowMinimap = boolVal; break;
                default:
                    // SkiaGraphView-specific settings
                    if (viewer is SkiaGraphView skia)
                    {
                        switch (option)
                        {
                            case "anti_alias": skia.AntiAlias = boolVal; break;
                            case "show_outlines": skia.ShowOutlines = boolVal; break;
                            case "show_levels_panel": skia.ShowLevelsPanel = boolVal; break;
                            case "show_parent_highlight": skia.ShowParentHighlight = boolVal; break;
                            case "show_off_screen_indicators": skia.ShowOffScreenIndicators = boolVal; break;
                            case "show_selection_glow": skia.ShowSelectionGlow = boolVal; break;
                            case "show_legend": skia.ShowLegend = boolVal; break;
                            case "lod_mode":
                                if (value is "auto" or "low" or "high")
                                    skia.LodMode = value;
                                else
                                    return $"Invalid lod_mode value: {value}. Use auto, low, or high.";
                                break;
                            default:
                                return $"Unknown option: {option}";
                        }
                    }
                    else
                    {
                        return $"Option '{option}' not supported by {viewer.ViewerType} viewer";
                    }
                    break;
            }

            viewer.ResetRenderStats();
            win.ViewerControl.Invalidate();
            return $"Set {option}={value}";
        });
    }

    // -----------------------------------------------------------------------
    // Color mode tools
    // -----------------------------------------------------------------------

    private string SetColorMode(JsonNode? args)
    {
        var win = GetWindow(args);
        string? nodeMode = OptArg(args, "node_mode");
        string? edgeMode = OptArg(args, "edge_mode");

        // Validate modes before touching UI
        if (nodeMode != null && !NodeColorProvider.NodeModes.Contains(nodeMode))
            return $"Invalid node_mode: {nodeMode}. Valid: {string.Join(", ", NodeColorProvider.NodeModes)}";
        if (edgeMode != null && !NodeColorProvider.EdgeModes.Contains(edgeMode))
            return $"Invalid edge_mode: {edgeMode}. Valid: {string.Join(", ", NodeColorProvider.EdgeModes)}";

        return InvokeOnUI(() =>
        {
            if (nodeMode != null)
                win.SetNodeColorMode(nodeMode);
            if (edgeMode != null)
                win.SetEdgeColorMode(edgeMode);

            return $"Color modes: node={win.NodeColorMode}, edge={win.EdgeColorMode}";
        });
    }

    private string GetColorMode(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
            $"node_mode={win.NodeColorMode}, edge_mode={win.EdgeColorMode}");
    }

    // -----------------------------------------------------------------------
    // Headless graph store tools
    // -----------------------------------------------------------------------

    private GraphModel GetGraph(JsonNode? args)
    {
        var graphId = Arg(args, "graph");
        if (_graphs.TryGetValue(graphId, out var graph))
            return graph;
        throw new ArgumentException($"Graph not found: {graphId}. Use graph_load first.");
    }

    private string GraphLoad(JsonNode? args)
    {
        var path = Arg(args, "path");
        var customId = OptArg(args, "graph_id");
        string graphId = customId ?? $"g{Interlocked.Increment(ref _graphCounter)}";

        Log($"    Loading graph '{graphId}' from {path}");
        var graph = GraphReader.ReadFile(path);
        graph.DetectCommunities();
        _graphs[graphId] = graph;

        return new JsonObject
        {
            ["graph_id"] = graphId,
            ["title"] = graph.Title,
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["directed"] = graph.IsDirected,
            ["communities"] = CountCommunities(graph),
        }.ToJsonString();
    }

    private string GraphList()
    {
        var list = new JsonArray();
        foreach (var (id, graph) in _graphs)
        {
            list.Add(new JsonObject
            {
                ["graph_id"] = id,
                ["title"] = graph.Title,
                ["nodes"] = graph.NodeCount,
                ["edges"] = graph.EdgeCount,
                ["directed"] = graph.IsDirected,
            });
        }
        return new JsonObject { ["graphs"] = list, ["count"] = list.Count }.ToJsonString();
    }

    private string GraphInfo(JsonNode? args)
    {
        var graph = GetGraph(args);
        return new JsonObject
        {
            ["title"] = graph.Title,
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["directed"] = graph.IsDirected,
            ["communities"] = CountCommunities(graph),
        }.ToJsonString();
    }

    private string GraphMetrics(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metric = Arg(args, "metric");
        int count = int.TryParse(OptArg(args, "count"), out int c) ? c : 20;
        string order = OptArg(args, "order") ?? "desc";
        int? community = int.TryParse(OptArg(args, "community"), out int comm) ? comm : null;

        var provider = new NodeColorProvider();
        provider.SetGraph(graph);
        double[] values = provider.ComputeNodeMetric(metric);

        var candidates = Enumerable.Range(0, graph.NodeCount);
        if (community.HasValue)
            candidates = candidates.Where(i => graph.Community[i] == community.Value);

        int[] sorted;
        if (order == "asc")
            sorted = candidates.OrderBy(i => values[i]).Take(count).ToArray();
        else
            sorted = candidates.OrderByDescending(i => values[i]).Take(count).ToArray();

        var nodes = new JsonArray();
        foreach (int i in sorted)
        {
            nodes.Add(new JsonObject
            {
                ["index"] = i,
                ["label"] = graph.Labels[i],
                ["community"] = graph.Community[i],
                [metric] = Math.Round(values[i], 6),
            });
        }

        return new JsonObject
        {
            ["metric"] = metric,
            ["order"] = order,
            ["count"] = sorted.Length,
            ["nodes"] = nodes,
        }.ToJsonString();
    }

    private string GraphStats(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metric = Arg(args, "metric");

        var provider = new NodeColorProvider();
        provider.SetGraph(graph);
        double[] values = provider.ComputeNodeMetric(metric);

        var stats = GraphAnalytics.Describe(values);
        return JsonSerializer.Serialize(new
        {
            metric,
            count = stats.Count,
            mean = Math.Round(stats.Mean, 6),
            std_dev = Math.Round(stats.StdDev, 6),
            variance = Math.Round(stats.Variance, 6),
            skewness = Math.Round(stats.Skewness, 6),
            kurtosis = Math.Round(stats.Kurtosis, 6),
            min = Math.Round(stats.Min, 6),
            max = Math.Round(stats.Max, 6),
            median = Math.Round(stats.Median, 6),
            q05 = Math.Round(stats.Q05, 6),
            q25 = Math.Round(stats.Q25, 6),
            q75 = Math.Round(stats.Q75, 6),
            q95 = Math.Round(stats.Q95, 6),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GraphHistogram(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metric = Arg(args, "metric");
        int bins = int.TryParse(OptArg(args, "bins"), out int b) ? b : 20;

        var provider = new NodeColorProvider();
        provider.SetGraph(graph);
        double[] values = provider.ComputeNodeMetric(metric);

        var hist = GraphAnalytics.Histogram(values, bins);
        var binsArr = new JsonArray();
        for (int i = 0; i < hist.Counts.Length; i++)
        {
            binsArr.Add(new JsonObject
            {
                ["center"] = Math.Round(hist.BinCenters[i], 6),
                ["count"] = hist.Counts[i],
            });
        }

        return new JsonObject
        {
            ["metric"] = metric,
            ["bins"] = binsArr,
            ["total"] = values.Length,
        }.ToJsonString();
    }

    private string GraphDegreeDistribution(JsonNode? args)
    {
        var graph = GetGraph(args);
        var dist = GraphAnalytics.DegreeDistribution(graph);
        var (alpha, rSquared) = GraphAnalytics.PowerLawFit(graph);

        var pairs = new JsonArray();
        foreach (var (degree, count) in dist)
        {
            pairs.Add(new JsonObject
            {
                ["degree"] = degree,
                ["count"] = count,
            });
        }

        return new JsonObject
        {
            ["distribution"] = pairs,
            ["distinct_degrees"] = dist.Length,
            ["power_law"] = new JsonObject
            {
                ["alpha"] = Math.Round(alpha, 4),
                ["r_squared"] = Math.Round(rSquared, 4),
                ["is_power_law"] = rSquared > 0.8,
            },
        }.ToJsonString();
    }

    private string GraphCorrelate(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metricA = Arg(args, "metric_a");
        string metricB = Arg(args, "metric_b");

        var provider = new NodeColorProvider();
        provider.SetGraph(graph);
        double[] a = provider.ComputeNodeMetric(metricA);
        double[] b = provider.ComputeNodeMetric(metricB);

        double pearson = GraphAnalytics.PearsonCorrelation(a, b);
        double spearman = GraphAnalytics.SpearmanCorrelation(a, b);

        return JsonSerializer.Serialize(new
        {
            metric_a = metricA,
            metric_b = metricB,
            pearson = double.IsNaN(pearson) ? 0.0 : Math.Round(pearson, 6),
            spearman = double.IsNaN(spearman) ? 0.0 : Math.Round(spearman, 6),
            interpretation = Math.Abs(pearson) > 0.7 ? "strongly correlated" :
                             Math.Abs(pearson) > 0.4 ? "moderately correlated" :
                             "weakly correlated",
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GraphCorrelationMatrix(JsonNode? args)
    {
        var graph = GetGraph(args);
        var (names, matrix) = GraphAnalytics.MetricCorrelationMatrix(graph);

        var rows = new JsonArray();
        for (int i = 0; i < names.Length; i++)
        {
            var row = new JsonObject { ["metric"] = names[i] };
            for (int j = 0; j < names.Length; j++)
                row[names[j]] = Math.Round(matrix[i, j], 3);
            rows.Add(row);
        }

        return new JsonObject
        {
            ["metrics"] = JsonSerializer.SerializeToNode(names),
            ["matrix"] = rows,
        }.ToJsonString();
    }

    private string GraphFindBridges(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metricsStr = Arg(args, "metrics");
        var metrics = metricsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int topN = int.TryParse(OptArg(args, "top_n"), out int n) ? n : 20;

        var bridgeNodes = GraphAnalytics.FindCrossRankNodes(graph, metrics, topN);

        var nodes = new JsonArray();
        var provider = new NodeColorProvider();
        provider.SetGraph(graph);

        foreach (int i in bridgeNodes)
        {
            var node = new JsonObject
            {
                ["index"] = i,
                ["label"] = graph.Labels[i],
                ["community"] = graph.Community[i],
            };
            foreach (var m in metrics)
            {
                double[] vals = provider.ComputeNodeMetric(m);
                node[m] = Math.Round(vals[i], 6);
            }
            nodes.Add(node);
        }

        return new JsonObject
        {
            ["metrics"] = JsonSerializer.SerializeToNode(metrics),
            ["top_n"] = topN,
            ["bridge_count"] = bridgeNodes.Length,
            ["nodes"] = nodes,
        }.ToJsonString();
    }

    private string GraphNeighbors(JsonNode? args)
    {
        var graph = GetGraph(args);
        int nodeIdx = int.Parse(Arg(args, "node"));

        if (nodeIdx < 0 || nodeIdx >= graph.NodeCount)
            return $"Node index {nodeIdx} out of range [0, {graph.NodeCount})";

        var neighbors = GraphAnalytics.GetNeighbors(graph, nodeIdx);
        var list = new JsonArray();
        foreach (var nb in neighbors)
        {
            list.Add(new JsonObject
            {
                ["index"] = nb.Index,
                ["label"] = nb.Label,
                ["community"] = nb.Community,
                ["degree"] = nb.Degree,
            });
        }

        return new JsonObject
        {
            ["node"] = nodeIdx,
            ["label"] = graph.Labels[nodeIdx],
            ["neighbor_count"] = neighbors.Length,
            ["neighbors"] = list,
        }.ToJsonString();
    }

    private string GraphNodeProfile(JsonNode? args)
    {
        var graph = GetGraph(args);
        int nodeIdx = int.Parse(Arg(args, "node"));

        if (nodeIdx < 0 || nodeIdx >= graph.NodeCount)
            return $"Node index {nodeIdx} out of range [0, {graph.NodeCount})";

        var profile = GraphAnalytics.NodeProfile(graph, nodeIdx);
        var obj = new JsonObject();
        foreach (var (key, value) in profile)
        {
            if (value is int iv) obj[key] = iv;
            else if (value is double dv) obj[key] = dv;
            else obj[key] = value.ToString();
        }
        obj["neighbor_count"] = graph.Neighbors(nodeIdx).Length;

        return obj.ToJsonString();
    }

    private string GraphFft(JsonNode? args)
    {
        var graph = GetGraph(args);
        string metric = Arg(args, "metric");
        int maxPeaks = int.TryParse(OptArg(args, "max_peaks"), out int mp) ? mp : 10;

        var provider = new NodeColorProvider();
        provider.SetGraph(graph);
        double[] values = provider.ComputeNodeMetric(metric);

        var spectrum = GraphAnalytics.ComputeSpectrum(values, sortFirst: true);
        var peaks = GraphAnalytics.FindDominantFrequencies(spectrum, threshold: 0.1, maxPeaks: maxPeaks);

        var peakArr = new JsonArray();
        foreach (var (idx, freq, mag) in peaks)
        {
            peakArr.Add(new JsonObject
            {
                ["index"] = idx,
                ["frequency"] = Math.Round(freq, 6),
                ["magnitude"] = Math.Round(mag, 6),
                ["period"] = freq > 0 ? Math.Round(1.0 / freq, 2) : double.PositiveInfinity,
            });
        }

        return new JsonObject
        {
            ["metric"] = metric,
            ["signal_length"] = values.Length,
            ["fft_length"] = spectrum.Frequencies.Length,
            ["dominant_peaks"] = peakArr,
            ["interpretation"] = peaks.Length > 0
                ? $"Found {peaks.Length} dominant frequencies — suggests periodic structure in {metric} distribution."
                : $"No dominant frequencies found — {metric} distribution appears smooth/monotonic.",
        }.ToJsonString();
    }

    private string GraphShow(JsonNode? args)
    {
        var graph = GetGraph(args);
        var win = GetWindow(args);

        // Run layout
        var hierarchy = RunLayout(graph, 300, 0.05, "auto", false, "1:1");

        InvokeOnUI(() =>
        {
            if (hierarchy != null)
                win.Viewer.SetGraphWithHierarchy(graph, hierarchy);
            else
                win.Viewer.SetGraph(graph);

            var c = CountCommunities(graph);
            win.SetStatus(
                $"◇  {graph.Title}  —  {graph.NodeCount} nodes, " +
                $"{graph.EdgeCount} edges, {c} communities" +
                (hierarchy != null ? $", {hierarchy.LevelCount} levels" : "") +
                $"  [{win.Viewer.ViewerType}]");
        });

        return new JsonObject
        {
            ["window"] = win.WindowId,
            ["title"] = graph.Title,
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["status"] = "displayed",
        }.ToJsonString();
    }

    // -----------------------------------------------------------------------
    // Viewer type tools
    // -----------------------------------------------------------------------

    private string SetViewerType(JsonNode? args)
    {
        var win = GetWindow(args);
        string type = Arg(args, "type");
        if (type is not ("force" or "matrix"))
            return $"Invalid viewer type: {type}. Use 'force' or 'matrix'.";

        return InvokeOnUI(() =>
        {
            win.SetViewerType(type);
            return $"Switched to {type} viewer in {win.WindowId}";
        });
    }

    private string SetMatrixOrdering(JsonNode? args)
    {
        var win = GetWindow(args);
        string ordering = Arg(args, "ordering");
        return InvokeOnUI(() =>
        {
            if (win.Viewer is not MatrixView mv)
                return "Window is not in matrix view mode. Use set_viewer_type first.";
            mv.OrderingMode = ordering;
            return $"Set matrix ordering to '{ordering}'";
        });
    }

    private string SetColorRamp(JsonNode? args)
    {
        var win = GetWindow(args);
        string ramp = Arg(args, "ramp");
        return InvokeOnUI(() =>
        {
            if (win.Viewer is not MatrixView mv)
                return "Window is not in matrix view mode. Use set_viewer_type first.";
            mv.ColorRamp = ramp;
            return $"Set color ramp to '{ramp}'";
        });
    }

    private string SetLogScale(JsonNode? args)
    {
        var win = GetWindow(args);
        bool enabled = Arg(args, "enabled")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        return InvokeOnUI(() =>
        {
            if (win.Viewer is not MatrixView mv)
                return "Window is not in matrix view mode. Use set_viewer_type first.";
            mv.LogScale = enabled;
            return $"Log scale {(enabled ? "enabled" : "disabled")}";
        });
    }

    private string SetGamma(JsonNode? args)
    {
        var win = GetWindow(args);
        if (!double.TryParse(Arg(args, "gamma"), out double gamma))
            return "Invalid gamma value. Must be a number (0.1–5.0).";
        return InvokeOnUI(() =>
        {
            if (win.Viewer is not MatrixView mv)
                return "Window is not in matrix view mode. Use set_viewer_type first.";
            mv.Gamma = gamma;
            return $"Set gamma to {mv.Gamma:F2}";
        });
    }

    private string SetDensityFloor(JsonNode? args)
    {
        var win = GetWindow(args);
        if (!double.TryParse(Arg(args, "floor"), out double floor))
            return "Invalid floor value. Must be a number (0.0–0.9).";
        return InvokeOnUI(() =>
        {
            if (win.Viewer is not MatrixView mv)
                return "Window is not in matrix view mode. Use set_viewer_type first.";
            mv.DensityFloor = floor;
            return $"Set density floor to {mv.DensityFloor:F2}";
        });
    }

    // -----------------------------------------------------------------------
    // Process control
    // -----------------------------------------------------------------------

    private string ExitProcess()
    {
        Log("  exit_process requested — shutting down");
        // Send the result before exiting
        Task.Run(async () =>
        {
            await Task.Delay(100); // let the response flush
            Environment.Exit(0);
        });
        return "Shutting down";
    }

    // -----------------------------------------------------------------------
    // Graph tools
    // -----------------------------------------------------------------------

    private string LoadGraph(JsonNode? args, int iterations, double gravity, string layoutMode,
        bool bounded = false, string aspectRatio = "1:1")
    {
        var win = GetWindow(args);
        var path = Arg(args, "path");
        Log($"    Loading graph from {path} (bounded={bounded}, aspect={aspectRatio})");

        var graph = GraphReader.ReadFile(path);
        graph.DetectCommunities();

        var hierarchy = RunLayout(graph, iterations, gravity, layoutMode, bounded, aspectRatio);

        InvokeOnUI(() =>
        {
            if (hierarchy != null)
                win.Viewer.SetGraphWithHierarchy(graph, hierarchy);
            else
                win.Viewer.SetGraph(graph);

            var c = CountCommunities(graph);
            win.SetStatus(
                $"◇  {graph.Title}  —  {graph.NodeCount} nodes, " +
                $"{graph.EdgeCount} edges, {c} communities" +
                (hierarchy != null ? $", {hierarchy.LevelCount} levels" : "") +
                $"  [{win.Viewer.ViewerType}]");
        });

        var communities = CountCommunities(graph);
        var result = new JsonObject
        {
            ["window"] = win.WindowId,
            ["title"] = graph.Title,
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["directed"] = graph.IsDirected,
            ["communities"] = communities,
            ["bounded"] = bounded,
            ["aspect_ratio"] = aspectRatio,
            ["status"] = "loaded and displayed",
        };
        if (hierarchy != null)
            result["coarsening_levels"] = hierarchy.LevelCount;
        return result.ToJsonString();
    }

    private string GetGraphInfo(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.Viewer.GetGraph();
            if (graph == null) return "No graph loaded";
            return new JsonObject
            {
                ["title"] = graph.Title,
                ["nodes"] = graph.NodeCount,
                ["edges"] = graph.EdgeCount,
                ["directed"] = graph.IsDirected,
                ["communities"] = CountCommunities(graph),
                ["viewer_type"] = win.Viewer.ViewerType,
            }.ToJsonString();
        });
    }

    private string RerunLayout(JsonNode? args, int iterations, string layoutMode,
        bool bounded = false, string aspectRatio = "1:1")
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.Viewer.GetGraph();
            if (graph == null) return "No graph loaded";

            var hierarchy = RunLayout(graph, iterations, 0.05, layoutMode, bounded, aspectRatio);
            if (hierarchy != null)
                win.Viewer.SetGraphWithHierarchy(graph, hierarchy);
            else
            {
                win.Viewer.AutoFit();
                win.ViewerControl.Invalidate();
            }
            return "Layout recomputed" +
                (hierarchy != null ? $" ({hierarchy.LevelCount} levels)" : "");
        });
    }

    /// <summary>
    /// Run layout on a graph using the specified mode.
    /// "auto": multi-level for n>200, flat otherwise.
    /// "multilevel": always multi-level.
    /// "flat": always flat FR.
    /// Returns the coarsening hierarchy if multi-level was used, null otherwise.
    /// </summary>
    private static CoarseningHierarchy? RunLayout(GraphModel graph, int iterations, double gravity, string mode,
        bool bounded = false, string aspectRatio = "1:1")
    {
        var (w, h) = bounded ? ParseAspectRatio(aspectRatio) : (1000.0, 1000.0);

        bool useMultiLevel = mode switch
        {
            "multilevel" => true,
            "flat" => false,
            _ => graph.NodeCount > 200, // "auto"
        };

        if (useMultiLevel)
        {
            var ml = new MultiLevelLayout(graph)
            {
                Width = w,
                Height = h,
                Gravity = gravity,
                CoarseIterations = Math.Max(iterations, 500),
                RefineIterations = Math.Min(iterations, 50),
                Bounded = bounded,
            };
            ml.Run();
            return ml.Hierarchy;
        }
        else
        {
            var layout = new ForceLayout(graph)
            {
                Width = w,
                Height = h,
                Iterations = iterations,
                Gravity = gravity,
                Bounded = bounded,
            };
            layout.Run();
            return null;
        }
    }

    private string AutoFitWindow(JsonNode? args)
    {
        var win = GetWindow(args);
        InvokeOnUI(() =>
        {
            win.Viewer.AutoFit();
            win.ViewerControl.Invalidate();
        });
        return "OK";
    }

    // -----------------------------------------------------------------------
    // Visual settings tools
    // -----------------------------------------------------------------------

    private string SetShowLabels(JsonNode? args)
    {
        var win = GetWindow(args);
        bool show = Arg(args, "show")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        InvokeOnUI(() =>
        {
            win.Viewer.ShowLabels = show;
            win.ViewerControl.Invalidate();
        });
        return "OK";
    }

    private string SetNodeRadius(JsonNode? args)
    {
        var win = GetWindow(args);
        if (float.TryParse(Arg(args, "radius"),
            System.Globalization.CultureInfo.InvariantCulture, out float r))
        {
            InvokeOnUI(() =>
            {
                win.Viewer.NodeRadius = r;
                win.ViewerControl.Invalidate();
            });
        }
        return "OK";
    }

    private string SetEdgeAlpha(JsonNode? args)
    {
        var win = GetWindow(args);
        if (float.TryParse(Arg(args, "alpha"),
            System.Globalization.CultureInfo.InvariantCulture, out float a))
        {
            InvokeOnUI(() =>
            {
                win.Viewer.EdgeAlpha = a;
                win.ViewerControl.Invalidate();
            });
        }
        return "OK";
    }

    private string SetLabelText(JsonNode? args)
    {
        var win = GetWindow(args);
        var text = Arg(args, "text");

        // Command channel: "!render ..." for perf experiments
        if (text.StartsWith("!render ", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = text.Substring(8).Trim();
            return HandleRenderCommand(win, cmd);
        }

        InvokeOnUI(() => win.SetStatus(text));
        return "OK";
    }

    /// <summary>
    /// Dispatch render commands via set_label_text (since MCP client caches tool list).
    /// Commands: "stats", "reset", "set OPTION VALUE", "warmup N"
    /// </summary>
    private string HandleRenderCommand(VizWindow win, string cmd)
    {
        if (cmd.Equals("stats", StringComparison.OrdinalIgnoreCase))
            return GetRenderStats(win);

        if (cmd.Equals("reset", StringComparison.OrdinalIgnoreCase))
            return ResetRenderStats(win);

        if (cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd.Substring(4).Trim().Split(' ', 2);
            if (parts.Length == 2)
                return SetRenderOptionDirect(win, parts[0].Trim(), parts[1].Trim());
            return "Usage: !render set OPTION VALUE";
        }

        if (cmd.StartsWith("warmup", StringComparison.OrdinalIgnoreCase))
        {
            // Trigger N repaints for warm-up, then return stats
            var parts = cmd.Split(' ', 2);
            int count = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 20;
            return InvokeOnUI(() =>
            {
                win.Viewer.ResetRenderStats();
                for (int i = 0; i < count; i++)
                {
                    win.ViewerControl.Refresh(); // synchronous paint
                }
                var stats = win.Viewer.GetRenderStats();
                return JsonSerializer.Serialize(stats,
                    new JsonSerializerOptions { WriteIndented = true });
            });
        }

        return "Unknown render command. Use: stats, reset, set OPTION VALUE, warmup [N]";
    }

    private string GetRenderStats(VizWindow win)
    {
        return InvokeOnUI(() =>
        {
            var stats = win.Viewer.GetRenderStats();
            return JsonSerializer.Serialize(stats,
                new JsonSerializerOptions { WriteIndented = true });
        });
    }

    private string ResetRenderStats(VizWindow win)
    {
        return InvokeOnUI(() =>
        {
            win.Viewer.ResetRenderStats();
            win.ViewerControl.Invalidate();
            return "Render stats reset.";
        });
    }

    private string SetRenderOptionDirect(VizWindow win, string option, string value)
    {
        return InvokeOnUI(() =>
        {
            var viewer = win.Viewer;
            bool boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

            switch (option)
            {
                case "show_edges": viewer.ShowEdges = boolVal; break;
                case "show_nodes": viewer.ShowNodes = boolVal; break;
                case "show_labels": viewer.ShowLabels = boolVal; break;
                case "show_minimap": viewer.ShowMinimap = boolVal; break;
                default:
                    if (viewer is SkiaGraphView skia)
                    {
                        switch (option)
                        {
                            case "anti_alias": skia.AntiAlias = boolVal; break;
                            case "show_outlines": skia.ShowOutlines = boolVal; break;
                            case "show_levels_panel": skia.ShowLevelsPanel = boolVal; break;
                            case "show_parent_highlight": skia.ShowParentHighlight = boolVal; break;
                            case "show_off_screen_indicators": skia.ShowOffScreenIndicators = boolVal; break;
                            case "show_selection_glow": skia.ShowSelectionGlow = boolVal; break;
                            case "show_legend": skia.ShowLegend = boolVal; break;
                            case "lod_mode":
                                if (value is "auto" or "low" or "high")
                                    skia.LodMode = value;
                                else
                                    return $"Invalid lod_mode: {value}. Use auto, low, high.";
                                break;
                            default:
                                return $"Unknown option: {option}";
                        }
                    }
                    else
                    {
                        return $"Option '{option}' not supported by {viewer.ViewerType} viewer";
                    }
                    break;
            }

            viewer.ResetRenderStats();
            win.ViewerControl.Invalidate();
            return $"Set {option}={value}";
        });
    }

    private string SetLabelColor(JsonNode? args)
    {
        var win = GetWindow(args);
        var color = ParseColor(Arg(args, "color"));
        InvokeOnUI(() => win.ForeColor = color);
        return "OK";
    }

    // -----------------------------------------------------------------------
    // Data tools
    // -----------------------------------------------------------------------

    private static string ListDataFiles()
    {
        var dataDir = @"c:\lumen-viz\data";
        if (!Directory.Exists(dataDir))
            return "No data directory found";

        var files = GraphReader.FindGraphFiles(dataDir).OrderBy(f => f).ToArray();
        var list = new JsonArray();
        foreach (var f in files)
            list.Add(f);
        return new JsonObject { ["files"] = list, ["count"] = files.Length }.ToJsonString();
    }

    // -----------------------------------------------------------------------
    // Window lookup
    // -----------------------------------------------------------------------

    private VizWindow GetWindow(JsonNode? args)
    {
        var winId = Arg(args, "window");
        if (_windows.TryGetValue(winId, out var win))
            return win;

        // Auto-create: if the window doesn't exist, create it on the fly.
        // This makes all tools self-sufficient — no separate create_window
        // call needed (especially useful when tool discovery has limits).
        Log($"    Auto-creating window {winId}");
        VizWindow? created = null;
        InvokeOnUI(() =>
        {
            created = new VizWindow(winId, "Lumen Viz  ◇", 1280, 800);
            created.WindowClosed += OnWindowClosed;
            _windows[winId] = created;
            created.Show();
        });
        return created ?? throw new InvalidOperationException(
            $"Failed to create window: {winId}");
    }

    /// <summary>
    /// Count distinct community IDs without LINQ allocation.
    /// </summary>
    private static int CountCommunities(GraphModel graph)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < graph.NodeCount; i++)
            seen.Add(graph.Community[i]);
        return seen.Count;
    }

    // -----------------------------------------------------------------------
    // UI thread bridge
    // -----------------------------------------------------------------------

    private void InvokeOnUI(Action action)
    {
        if (_invokeHelper != null && !_invokeHelper.IsDisposed
            && _invokeHelper.InvokeRequired)
        {
            _invokeHelper.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private T InvokeOnUI<T>(Func<T> func)
    {
        if (_invokeHelper != null && !_invokeHelper.IsDisposed
            && _invokeHelper.InvokeRequired)
        {
            return (T)_invokeHelper.Invoke(func);
        }
        return func();
    }

    // -----------------------------------------------------------------------
    // JSON-RPC response helpers
    // -----------------------------------------------------------------------

    private void SendResult(JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        Send(response);
    }

    private void SendError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        Send(response);
    }

    private void SendToolResult(JsonNode? id, string text)
    {
        SendResult(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
        });
    }

    private void SendToolError(JsonNode? id, string message)
    {
        SendResult(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Error: {message}",
                },
            },
            ["isError"] = true,
        });
    }

    private void Send(JsonNode message)
    {
        var json = message.ToJsonString(
            new JsonSerializerOptions { WriteIndented = false });
        Log($"--> {json[..Math.Min(json.Length, 120)]}");
        lock (_writeLock)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }

    // -----------------------------------------------------------------------
    // Argument helpers
    // -----------------------------------------------------------------------

    private static string Arg(JsonNode? args, string name)
    {
        return args?[name]?.GetValue<string>()
            ?? throw new ArgumentException($"Missing argument: {name}");
    }

    private static string? NodeToString(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            // Handle numbers, booleans sent as non-string JSON types
            return jv.ToJsonString();
        }
        return node.ToJsonString();
    }

    private static string? OptArg(JsonNode? args, string name)
    {
        return NodeToString(args?[name]);
    }

    private static string ArgOr(JsonNode? args, string name, string fallback)
    {
        return NodeToString(args?[name]) ?? fallback;
    }

    private static int IntArg(JsonNode? args, string name, int fallback)
    {
        var node = args?[name];
        if (node == null) return fallback;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        var s = NodeToString(node);
        return s != null && int.TryParse(s, out var v) ? v : fallback;
    }

    private static double DoubleArg(JsonNode? args, string name, double fallback)
    {
        var node = args?[name];
        if (node == null) return fallback;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        var s = NodeToString(node);
        return s != null && double.TryParse(s,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }

    private static bool BoolArg(JsonNode? args, string name, bool fallback)
    {
        var s = args?[name]?.GetValue<string>();
        if (s == null) return fallback;
        return s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parse "W:H" aspect ratio string into (width, height) layout dimensions.</summary>
    private static (double w, double h) ParseAspectRatio(string ratio, double baseSize = 1000)
    {
        var parts = ratio.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var rw)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var rh)
            && rw > 0 && rh > 0)
        {
            // Scale so the larger dimension = baseSize
            double maxDim = Math.Max(rw, rh);
            return (baseSize * rw / maxDim, baseSize * rh / maxDim);
        }
        return (baseSize, baseSize);
    }

    private static Color ParseColor(string s)
    {
        if (s.StartsWith('#') && (s.Length == 7 || s.Length == 9))
            return ColorTranslator.FromHtml(s);
        var color = Color.FromName(s);
        if (color.IsKnownColor || color.ToArgb() != 0)
            return color;
        throw new ArgumentException($"Unknown color: {s}");
    }

    // -----------------------------------------------------------------------
    // Logging (stderr)
    // -----------------------------------------------------------------------

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[mcp] {message}");
    }

    // -----------------------------------------------------------------------
    // Graph partitioning tools
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve a graph from either the headless store ("graphname") or
    /// a window ("window:winId").
    /// </summary>
    private GraphModel ResolveGraph(string graphRef)
    {
        if (graphRef.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
        {
            var winId = graphRef.Substring("window:".Length);
            if (_windows.TryGetValue(winId, out var win))
            {
                var g = InvokeOnUI(() => win.Viewer.GetGraph());
                if (g != null) return g;
                throw new ArgumentException($"Window '{winId}' has no graph loaded.");
            }
            throw new ArgumentException($"Window not found: {winId}");
        }

        if (_graphs.TryGetValue(graphRef, out var graph))
            return graph;

        throw new ArgumentException(
            $"Graph not found: {graphRef}. Use graph_load or 'window:<id>'.");
    }

    private static LumenGraph.Partition.PartitionOptions BuildPartitionOptions(JsonNode? args)
    {
        var opts = new LumenGraph.Partition.PartitionOptions
        {
            TargetSplit = DoubleArg(args, "target_split", 0.5),
            BalanceTolerance = DoubleArg(args, "tolerance", 0.25),
        };

        var matchStr = OptArg(args, "matching")?.ToLowerInvariant();
        if (matchStr != null)
        {
            opts.Matching = matchStr switch
            {
                "random" => LumenGraph.Partition.MatchingStrategy.Random,
                "hem" => LumenGraph.Partition.MatchingStrategy.HEM,
                "hemsr" => LumenGraph.Partition.MatchingStrategy.HEMSR,
                "hemsrdeg" => LumenGraph.Partition.MatchingStrategy.HEMSRdeg,
                _ => throw new ArgumentException(
                    $"Unknown matching strategy: {matchStr}. " +
                    "Use 'random', 'hem', 'hemsr', or 'hemsrdeg'."),
            };
        }

        return opts;
    }

    private string GraphBisect(JsonNode? args)
    {
        var graphRef = Arg(args, "graph");
        var graph = ResolveGraph(graphRef);
        var opts = BuildPartitionOptions(args);

        Log($"    Bisecting graph ({graph.NodeCount} nodes, {graph.EdgeCount} edges)");

        var result = LumenGraph.Partition.EdgeSeparator.Bisect(graph, opts);

        return new JsonObject
        {
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["edge_cut"] = result.EdgeCut,
            ["size_a"] = result.SizeA,
            ["size_b"] = result.SizeB,
            ["imbalance"] = Math.Round(result.Imbalance, 4),
            ["partition"] = new JsonArray(
                result.Side.Select(s => (JsonNode)JsonValue.Create(s ? 1 : 0)).ToArray()),
        }.ToJsonString();
    }

    private string GraphPartition(JsonNode? args)
    {
        var graphRef = Arg(args, "graph");
        var graph = ResolveGraph(graphRef);
        var opts = BuildPartitionOptions(args);
        int k = IntArg(args, "k", 2);

        Log($"    {k}-way partitioning graph ({graph.NodeCount} nodes, {graph.EdgeCount} edges)");

        var partIds = LumenGraph.Partition.EdgeSeparator.RecursiveBisect(graph, k, opts);

        // Compute summary: size of each partition and total edge cut
        var sizes = new int[k];
        for (int i = 0; i < partIds.Length; i++)
            if (partIds[i] < k) sizes[partIds[i]]++;

        double totalCut = 0;
        for (int e = 0; e < graph.EdgeCount; e++)
        {
            if (partIds[graph.EdgeSource[e]] != partIds[graph.EdgeTarget[e]])
                totalCut += graph.EdgeWeight[e];
        }

        var sizesArray = new JsonArray(
            sizes.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        var partArray = new JsonArray(
            partIds.Select(p => (JsonNode)JsonValue.Create(p)).ToArray());

        return new JsonObject
        {
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["k"] = k,
            ["edge_cut"] = totalCut,
            ["partition_sizes"] = sizesArray,
            ["partition"] = partArray,
        }.ToJsonString();
    }
}
