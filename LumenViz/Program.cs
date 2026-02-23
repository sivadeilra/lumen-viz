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

        bool mcpMode = args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

        if (mcpMode)
        {
            // Windowless MCP mode: no form on startup.
            // MCP tools create/manage windows on demand.
            var stdoutWriter = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
            };
            var stdinReader = new StreamReader(Console.OpenStandardInput());

            var appContext = new ApplicationContext();
            var server = new McpServer(appContext, stdinReader, stdoutWriter);

            // Create the invoke helper on the UI thread, then start MCP.
            server.CreateInvokeHelper();
            server.Start();

            Application.Run(appContext);
        }
        else
        {
            // Interactive mode: show a default window.
            var form = new MainForm();
            Application.Run(form);
        }
    }
}