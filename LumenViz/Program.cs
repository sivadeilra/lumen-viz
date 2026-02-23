namespace LumenViz;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var form = new MainForm();
        bool mcpMode = args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

        if (mcpMode)
        {
            // MCP mode: read JSON-RPC from stdin, write to stdout.
            // Detach Console.Out so nothing else accidentally writes to it.
            var stdoutWriter = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
            };
            var stdinReader = new StreamReader(Console.OpenStandardInput());

            var server = new McpServer(form, stdinReader, stdoutWriter);
            server.Start();
        }

        Application.Run(form);
    }
}