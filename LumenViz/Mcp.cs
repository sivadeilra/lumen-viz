using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

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
                "Load a graph from a Matrix Market (.mtx) file into a window. " +
                "Runs community detection and force-directed layout automatically.",
                PropsReq(("window", "string", "Window ID"),
                      ("path", "string", "Absolute path to a .mtx file"))),

            ToolDef("load_graph_with_layout",
                "Load a graph with custom layout parameters.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("path", "string", "Absolute path to a .mtx file", true),
                    ("iterations", "string", "Layout iterations (default 300)", false),
                    ("gravity", "string", "Gravity constant (default 0.05)", false))),

            ToolDef("get_graph_info",
                "Get info about the graph loaded in a window.",
                PropsReq(("window", "string", "Window ID"))),

            ToolDef("rerun_layout",
                "Re-run force-directed layout with fresh random positions.",
                Props(
                    ("window", "string", "Window ID", true),
                    ("iterations", "string", "Number of iterations (default 300)", false))),

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
                "List available .mtx data files in c:\\lumen-viz\\data.",
                Props()),
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
                "load_graph" => LoadGraph(args, 300, 0.05),
                "load_graph_with_layout" => LoadGraph(args,
                    IntArg(args, "iterations", 300),
                    DoubleArg(args, "gravity", 0.05)),
                "get_graph_info" => GetGraphInfo(args),
                "rerun_layout" => RerunLayout(args, IntArg(args, "iterations", 300)),
                "auto_fit" => AutoFitWindow(args),

                // ── Visual settings ────────────────────────────────────
                "set_show_labels" => SetShowLabels(args),
                "set_node_radius" => SetNodeRadius(args),
                "set_edge_alpha" => SetEdgeAlpha(args),
                "set_label_text" => SetLabelText(args),
                "set_label_color" => SetLabelColor(args),

                // ── Data ───────────────────────────────────────────────
                "list_data_files" => ListDataFiles(),

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
            }
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
    // Graph tools
    // -----------------------------------------------------------------------

    private string LoadGraph(JsonNode? args, int iterations, double gravity)
    {
        var win = GetWindow(args);
        var path = Arg(args, "path");
        Log($"    Loading graph from {path}");

        var graph = MatrixMarketReader.ReadFile(path);
        graph.DetectCommunities();

        var layout = new ForceLayout(graph)
        {
            Width = 1000,
            Height = 1000,
            Iterations = iterations,
            Gravity = gravity,
        };
        layout.Run();

        InvokeOnUI(() =>
        {
            win.GraphView.SetGraph(graph);
            var c = CountCommunities(graph);
            win.SetStatus(
                $"◇  {graph.Title}  —  {graph.NodeCount} nodes, " +
                $"{graph.EdgeCount} edges, {c} communities");
        });

        var communities = CountCommunities(graph);
        return new JsonObject
        {
            ["window"] = win.WindowId,
            ["title"] = graph.Title,
            ["nodes"] = graph.NodeCount,
            ["edges"] = graph.EdgeCount,
            ["communities"] = communities,
            ["status"] = "loaded and displayed",
        }.ToJsonString();
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
                ["communities"] = CountCommunities(graph),
            }.ToJsonString();
        });
    }

    private string RerunLayout(JsonNode? args, int iterations)
    {
        var win = GetWindow(args);
        return InvokeOnUI(() =>
        {
            var graph = win.GraphView.GetGraph();
            if (graph == null) return "No graph loaded";

            var layout = new ForceLayout(graph)
            {
                Width = 1000,
                Height = 1000,
                Iterations = iterations,
            };
            layout.Randomize();
            layout.Run();
            win.GraphView.AutoFit();
            win.GraphView.Invalidate();
            return "Layout recomputed";
        });
    }

    private string AutoFitWindow(JsonNode? args)
    {
        var win = GetWindow(args);
        InvokeOnUI(() =>
        {
            win.GraphView.AutoFit();
            win.GraphView.Invalidate();
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
        InvokeOnUI(() => win.SetStatus(text));
        return "OK";
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

        var files = Directory.GetFiles(dataDir, "*.mtx", SearchOption.AllDirectories);
        var list = new JsonArray();
        foreach (var f in files)
            list.Add(f);
        return new JsonObject { ["files"] = list }.ToJsonString();
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
