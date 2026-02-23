using System.Text.Json;
using System.Text.Json.Nodes;

namespace LumenViz;

/// <summary>
/// Minimal MCP (Model Context Protocol) server.
/// Reads JSON-RPC 2.0 from stdin, dispatches tool calls to the WinForms
/// UI thread via Control.Invoke(), and writes responses to stdout.
/// </summary>
public class McpServer
{
    private readonly MainForm _form;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();

    public McpServer(MainForm form, TextReader input, TextWriter output)
    {
        _form = form;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Start the MCP read loop on a background thread.
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

    // -----------------------------------------------------------------------
    // Read loop
    // -----------------------------------------------------------------------

    private void ReadLoop()
    {
        Log("MCP server started, reading stdin...");
        try
        {
            while (true)
            {
                var line = _input.ReadLine();
                if (line == null) break; // stdin closed
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
        _form.Invoke(() => _form.Close());
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
                        ["version"] = "0.1.0",
                    },
                });
                break;

            case "notifications/initialized":
                // Notification — no response.
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
    // Tool registry
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
        params (string name, string type, string description)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc) in properties)
        {
            props[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = desc,
            };
            required.Add(name);
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

    private static JsonArray BuildToolList()
    {
        return new JsonArray
        {
            ToolDef("set_label_text",
                "Set the text of the hello-world label.",
                Props(("text", "string", "The text to display"))),

            ToolDef("get_label_text",
                "Get the current text of the hello-world label.",
                Props()),

            ToolDef("set_label_color",
                "Set the foreground color of the hello-world label.",
                Props(("color", "string",
                    "A named color (Red, Blue, ...) or hex (#RRGGBB)"))),

            ToolDef("set_textbox_text",
                "Set the text of the text box.",
                Props(("text", "string", "The text to set"))),

            ToolDef("get_textbox_text",
                "Get the current text of the text box.",
                Props()),

            ToolDef("set_window_title",
                "Set the window title bar text.",
                Props(("title", "string", "The new window title"))),

            ToolDef("get_window_info",
                "Get information about the window (title, size, etc.).",
                Props()),

            ToolDef("show_message",
                "Show a message box to the user.",
                Props(("text", "string", "Message body"),
                      ("caption", "string", "Dialog title"))),

            // ── Graph visualization tools ──────────────────────────
            ToolDef("load_graph",
                "Load a graph from a Matrix Market (.mtx) file and display it with force-directed layout.",
                Props(("path", "string", "Absolute path to a .mtx file"))),

            ToolDef("load_graph_with_layout",
                "Load a graph from a .mtx file and run layout with custom parameters.",
                Props(("path", "string", "Absolute path to a .mtx file"),
                      ("iterations", "string", "Number of layout iterations (default 300)"),
                      ("gravity", "string", "Gravity constant (default 0.05)"))),

            ToolDef("get_graph_info",
                "Get info about the currently loaded graph (nodes, edges, communities).",
                Props()),

            ToolDef("set_show_labels",
                "Toggle node label display.",
                Props(("show", "string", "true or false"))),

            ToolDef("set_node_radius",
                "Set the base node radius in pixels.",
                Props(("radius", "string", "Radius (e.g. 4, 6, 10)"))),

            ToolDef("set_edge_alpha",
                "Set edge transparency (0.0 = invisible, 1.0 = opaque).",
                Props(("alpha", "string", "Alpha value 0.0-1.0"))),

            ToolDef("auto_fit",
                "Re-center and zoom to fit the graph in the view.",
                Props()),

            ToolDef("rerun_layout",
                "Re-run force-directed layout on the current graph.",
                Props(("iterations", "string", "Number of iterations (default 300)"))),

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
                "set_label_text" => InvokeOnUI(() =>
                {
                    _form.HelloLabel.Text = Arg(args, "text");
                    return "OK";
                }),

                "get_label_text" => InvokeOnUI(() =>
                    _form.HelloLabel.Text ?? ""),

                "set_label_color" => InvokeOnUI(() =>
                {
                    var colorStr = Arg(args, "color");
                    var color = ParseColor(colorStr);
                    _form.HelloLabel.ForeColor = color;
                    return $"Color set to {color.Name}";
                }),

                "set_textbox_text" => InvokeOnUI(() =>
                {
                    _form.InputTextBox.Text = Arg(args, "text");
                    return "OK";
                }),

                "get_textbox_text" => InvokeOnUI(() =>
                    _form.InputTextBox.Text ?? ""),

                "set_window_title" => InvokeOnUI(() =>
                {
                    _form.Text = Arg(args, "title");
                    return "OK";
                }),

                "get_window_info" => InvokeOnUI(() =>
                {
                    var info = new JsonObject
                    {
                        ["title"] = _form.Text,
                        ["width"] = _form.ClientSize.Width,
                        ["height"] = _form.ClientSize.Height,
                        ["windowState"] = _form.WindowState.ToString(),
                    };
                    return info.ToJsonString();
                }),

                "show_message" => InvokeOnUI(() =>
                {
                    var text = Arg(args, "text");
                    var caption = ArgOr(args, "caption", "Lumen Viz");
                    MessageBox.Show(_form, text, caption,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return "OK";
                }),

                // ── Graph visualization tools ──────────────────────────

                "load_graph" => LoadGraph(args, iterations: 300, gravity: 0.05),

                "load_graph_with_layout" => LoadGraph(args,
                    int.TryParse(ArgOr(args, "iterations", "300"), out var it) ? it : 300,
                    double.TryParse(ArgOr(args, "gravity", "0.05"),
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var gv) ? gv : 0.05),

                "get_graph_info" => InvokeOnUI(() =>
                {
                    var graph = _form.GraphView.GetGraph();
                    if (graph == null) return "No graph loaded";
                    var communities = graph.Nodes.Select(n => n.Community).Distinct().Count();
                    var info = new JsonObject
                    {
                        ["title"] = graph.Title,
                        ["nodes"] = graph.Nodes.Count,
                        ["edges"] = graph.Edges.Count,
                        ["communities"] = communities,
                    };
                    return info.ToJsonString();
                }),

                "set_show_labels" => InvokeOnUI(() =>
                {
                    _form.GraphView.ShowLabels = Arg(args, "show")
                        .Equals("true", StringComparison.OrdinalIgnoreCase);
                    _form.GraphView.Invalidate();
                    return "OK";
                }),

                "set_node_radius" => InvokeOnUI(() =>
                {
                    if (float.TryParse(Arg(args, "radius"),
                        System.Globalization.CultureInfo.InvariantCulture, out float r))
                    {
                        _form.GraphView.NodeRadius = r;
                        _form.GraphView.Invalidate();
                    }
                    return "OK";
                }),

                "set_edge_alpha" => InvokeOnUI(() =>
                {
                    if (float.TryParse(Arg(args, "alpha"),
                        System.Globalization.CultureInfo.InvariantCulture, out float a))
                    {
                        _form.GraphView.EdgeAlpha = a;
                        _form.GraphView.Invalidate();
                    }
                    return "OK";
                }),

                "auto_fit" => InvokeOnUI(() =>
                {
                    _form.GraphView.AutoFit();
                    _form.GraphView.Invalidate();
                    return "OK";
                }),

                "rerun_layout" => RerunLayout(
                    int.TryParse(ArgOr(args, "iterations", "300"), out var ri) ? ri : 300),

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
    // Graph tool helpers
    // -----------------------------------------------------------------------

    private string LoadGraph(JsonNode? args, int iterations, double gravity)
    {
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
            _form.GraphView.SetGraph(graph);
            var communities = graph.Nodes.Select(n => n.Community).Distinct().Count();
            _form.SetStatus(
                $"◇  {graph.Title}  —  {graph.Nodes.Count} nodes, " +
                $"{graph.Edges.Count} edges, {communities} communities");
            return 0;
        });

        var communities2 = graph.Nodes.Select(n => n.Community).Distinct().Count();
        var info = new JsonObject
        {
            ["title"] = graph.Title,
            ["nodes"] = graph.Nodes.Count,
            ["edges"] = graph.Edges.Count,
            ["communities"] = communities2,
            ["status"] = "loaded and displayed",
        };
        return info.ToJsonString();
    }

    private string RerunLayout(int iterations)
    {
        return InvokeOnUI(() =>
        {
            var graph = _form.GraphView.GetGraph();
            if (graph == null) return "No graph loaded";

            var layout = new ForceLayout(graph)
            {
                Width = 1000,
                Height = 1000,
                Iterations = iterations,
            };
            layout.Randomize();
            layout.Run();
            _form.GraphView.AutoFit();
            _form.GraphView.Invalidate();
            return "Layout recomputed";
        });
    }

    private static string ListDataFiles()
    {
        var dataDir = @"c:\lumen-viz\data";
        if (!Directory.Exists(dataDir))
            return "No data directory found";

        var files = Directory.GetFiles(dataDir, "*.mtx", SearchOption.AllDirectories);
        var list = new JsonArray();
        foreach (var f in files)
        {
            list.Add(f);
        }
        var result = new JsonObject { ["files"] = list };
        return result.ToJsonString();
    }

    // -----------------------------------------------------------------------
    // UI thread bridge
    // -----------------------------------------------------------------------

    private T InvokeOnUI<T>(Func<T> func)
    {
        if (_form.InvokeRequired)
            return (T)_form.Invoke(func);
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
    // Logging (stderr — per MCP convention)
    // -----------------------------------------------------------------------

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[mcp] {message}");
    }
}
