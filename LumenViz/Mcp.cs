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
                    ("height", "string", "Client height in pixels (default 800)", false))),

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
        };
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
        int num = Interlocked.Increment(ref _windowCounter);
        string winId = $"win{num}";

        InvokeOnUI(() =>
        {
            var window = new VizWindow(winId, title, width, height);
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
                    ["has_graph"] = win.GraphView.GetGraph() != null,
                    ["selected"] = win.GraphView.Selection.Count,
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
            var graph = win.GraphView.GetGraph();
            var info = new JsonObject
            {
                ["id"] = win.WindowId,
                ["title"] = win.Text,
                ["width"] = win.ClientSize.Width,
                ["height"] = win.ClientSize.Height,
                ["windowState"] = win.WindowState.ToString(),
            };
            if (graph != null)
            {
                info["graph_title"] = graph.Title;
                info["graph_nodes"] = graph.NodeCount;
                info["graph_edges"] = graph.EdgeCount;
                info["graph_communities"] = CountCommunities(graph);
                info["selected_nodes"] = win.GraphView.Selection.Count;

                var hierarchy = win.GraphView.GetHierarchy();
                if (hierarchy != null)
                {
                    info["coarsening_levels"] = hierarchy.LevelCount;
                    info["current_level"] = win.GraphView.CurrentLevel;
                }
            }

            // Include render performance stats
            var stats = win.GraphView.GetRenderStats();
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
            win.GraphView.AutoFit();
            win.GraphView.Invalidate();
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
                win.GraphView.AutoFit();
                win.GraphView.Invalidate();
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
            var graph = win.GraphView.GetGraph();
            if (graph == null) return "No graph loaded";

            var selection = win.GraphView.Selection;
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
                int count = win.GraphView.Selection.Count;
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

    // -----------------------------------------------------------------------
    // Coarsening level tools
    // -----------------------------------------------------------------------

    private string GetCoarseLevels(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var hierarchy = win.GraphView.GetHierarchy();
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
                    ["is_current"] = (i == win.GraphView.CurrentLevel),
                });
            }
            return new JsonObject
            {
                ["window"] = win.WindowId,
                ["current_level"] = win.GraphView.CurrentLevel,
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
            if (win.GraphView.SetLevel(level))
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
            var stats = win.UseSkia
                ? win.SkiaView.GetRenderStats()
                : win.GraphView.GetRenderStats();
            return System.Text.Json.JsonSerializer.Serialize(stats,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });
    }

    private string ResetRenderStats(JsonNode? args)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            if (win.UseSkia)
            {
                win.SkiaView.ResetRenderStats();
                win.SkiaView.Invalidate();
            }
            else
            {
                win.GraphView.ResetRenderStats();
                win.GraphView.Invalidate();
            }
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
            // Handle renderer switch first (applies at window level)
            if (option == "renderer")
            {
                bool skia = value.Equals("skia", StringComparison.OrdinalIgnoreCase);
                bool gdi = value.Equals("gdi", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("gdi+", StringComparison.OrdinalIgnoreCase);
                if (!skia && !gdi)
                    return $"Invalid renderer: {value}. Use 'skia' or 'gdi'.";
                win.SetRenderer(skia);
                return $"Switched to {(skia ? "Skia" : "GDI+")} renderer.";
            }

            var gv = win.GraphView;
            bool boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

            switch (option)
            {
                case "anti_alias": gv.AntiAlias = boolVal; break;
                case "show_edges": gv.ShowEdges = boolVal; break;
                case "show_nodes": gv.ShowNodes = boolVal; break;
                case "show_outlines": gv.ShowOutlines = boolVal; break;
                case "show_labels": gv.ShowLabels = boolVal; break;
                case "show_minimap": gv.ShowMinimap = boolVal; break;
                case "show_levels_panel": gv.ShowLevelsPanel = boolVal; break;
                case "show_parent_highlight": gv.ShowParentHighlight = boolVal; break;
                case "show_off_screen_indicators": gv.ShowOffScreenIndicators = boolVal; break;
                case "show_selection_glow": gv.ShowSelectionGlow = boolVal; break;
                case "lod_mode":
                    if (value is "auto" or "low" or "high")
                        gv.LodMode = value;
                    else
                        return $"Invalid lod_mode value: {value}. Use auto, low, or high.";
                    break;
                default:
                    return $"Unknown option: {option}";
            }

            // Apply the same setting to the Skia view too
            var sv = win.SkiaView;
            switch (option)
            {
                case "anti_alias": sv.AntiAlias = boolVal; break;
                case "show_edges": sv.ShowEdges = boolVal; break;
                case "show_nodes": sv.ShowNodes = boolVal; break;
                case "show_outlines": sv.ShowOutlines = boolVal; break;
                case "show_labels": sv.ShowLabels = boolVal; break;
                case "show_minimap": sv.ShowMinimap = boolVal; break;
                case "show_levels_panel": sv.ShowLevelsPanel = boolVal; break;
                case "show_parent_highlight": sv.ShowParentHighlight = boolVal; break;
                case "show_off_screen_indicators": sv.ShowOffScreenIndicators = boolVal; break;
                case "show_selection_glow": sv.ShowSelectionGlow = boolVal; break;
                case "lod_mode":
                    sv.LodMode = value;
                    break;
            }

            gv.ResetRenderStats(); // fresh stats after changing a setting
            sv.ResetRenderStats();
            gv.Invalidate();
            sv.Invalidate();
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
            // Load into the currently-active view. The inactive view
            // will get the graph when SetRenderer() is called.
            if (hierarchy != null)
            {
                if (win.UseSkia)
                    win.SkiaView.SetGraphWithHierarchy(graph, hierarchy);
                else
                    win.GraphView.SetGraphWithHierarchy(graph, hierarchy);
            }
            else
            {
                if (win.UseSkia)
                    win.SkiaView.SetGraph(graph);
                else
                    win.GraphView.SetGraph(graph);
            }

            var c = CountCommunities(graph);
            string renderer = win.UseSkia ? "Skia" : "GDI+";
            win.SetStatus(
                $"◇  {graph.Title}  —  {graph.NodeCount} nodes, " +
                $"{graph.EdgeCount} edges, {c} communities" +
                (hierarchy != null ? $", {hierarchy.LevelCount} levels" : "") +
                $"  [{renderer}]");
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
            var graph = win.GraphView.GetGraph();
            if (graph == null) return "No graph loaded";
            return new JsonObject
            {
                ["title"] = graph.Title,
                ["nodes"] = graph.NodeCount,
                ["edges"] = graph.EdgeCount,
                ["directed"] = graph.IsDirected,
                ["communities"] = CountCommunities(graph),
            }.ToJsonString();
        });
    }

    private string RerunLayout(JsonNode? args, int iterations, string layoutMode,
        bool bounded = false, string aspectRatio = "1:1")
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.GraphView.GetGraph();
            if (graph == null) return "No graph loaded";

            var hierarchy = RunLayout(graph, iterations, 0.05, layoutMode, bounded, aspectRatio);
            if (hierarchy != null)
                win.GraphView.SetGraphWithHierarchy(graph, hierarchy);
            else
            {
                win.GraphView.AutoFit();
                win.GraphView.Invalidate();
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
            if (win.UseSkia)
            {
                win.SkiaView.AutoFit();
                win.SkiaView.Invalidate();
            }
            else
            {
                win.GraphView.AutoFit();
                win.GraphView.Invalidate();
            }
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
            win.GraphView.ShowLabels = show;
            win.GraphView.Invalidate();
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
                win.GraphView.NodeRadius = r;
                win.GraphView.Invalidate();
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
                win.GraphView.EdgeAlpha = a;
                win.GraphView.Invalidate();
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
                var gv = win.GraphView;
                gv.ResetRenderStats();
                for (int i = 0; i < count; i++)
                {
                    gv.Refresh(); // synchronous paint
                }
                var stats = gv.GetRenderStats();
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
            var stats = win.GraphView.GetRenderStats();
            return JsonSerializer.Serialize(stats,
                new JsonSerializerOptions { WriteIndented = true });
        });
    }

    private string ResetRenderStats(VizWindow win)
    {
        return InvokeOnUI(() =>
        {
            win.GraphView.ResetRenderStats();
            win.GraphView.Invalidate();
            return "Render stats reset.";
        });
    }

    private string SetRenderOptionDirect(VizWindow win, string option, string value)
    {
        return InvokeOnUI(() =>
        {
            var gv = win.GraphView;
            bool boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

            switch (option)
            {
                case "anti_alias": gv.AntiAlias = boolVal; break;
                case "show_edges": gv.ShowEdges = boolVal; break;
                case "show_nodes": gv.ShowNodes = boolVal; break;
                case "show_outlines": gv.ShowOutlines = boolVal; break;
                case "show_labels": gv.ShowLabels = boolVal; break;
                case "show_minimap": gv.ShowMinimap = boolVal; break;
                case "show_levels_panel": gv.ShowLevelsPanel = boolVal; break;
                case "show_parent_highlight": gv.ShowParentHighlight = boolVal; break;
                case "show_off_screen_indicators": gv.ShowOffScreenIndicators = boolVal; break;
                case "show_selection_glow": gv.ShowSelectionGlow = boolVal; break;
                case "lod_mode":
                    if (value is "auto" or "low" or "high")
                        gv.LodMode = value;
                    else
                        return $"Invalid lod_mode: {value}. Use auto, low, high.";
                    break;
                default:
                    return $"Unknown option: {option}";
            }

            gv.ResetRenderStats();
            gv.Invalidate();
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

    private static string? OptArg(JsonNode? args, string name)
    {
        return args?[name]?.GetValue<string>();
    }

    private static string ArgOr(JsonNode? args, string name, string fallback)
    {
        return args?[name]?.GetValue<string>() ?? fallback;
    }

    private static int IntArg(JsonNode? args, string name, int fallback)
    {
        var s = args?[name]?.GetValue<string>();
        return s != null && int.TryParse(s, out var v) ? v : fallback;
    }

    private static double DoubleArg(JsonNode? args, string name, double fallback)
    {
        var s = args?[name]?.GetValue<string>();
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
}
