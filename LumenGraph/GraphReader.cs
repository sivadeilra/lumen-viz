namespace LumenGraph;

/// <summary>
/// Auto-detects graph file format by extension and dispatches to
/// the appropriate reader. Central entry point for loading graphs.
/// </summary>
public static class GraphReader
{
    /// <summary>
    /// Supported file extensions (lowercase, with leading dot).
    /// </summary>
    public static readonly string[] SupportedExtensions =
    [
        ".mtx",      // Matrix Market
        ".gml",      // Graph Modelling Language
        ".graphml",  // GraphML (XML)
        ".csv",      // Edge list (comma-separated)
        ".tsv",      // Edge list (tab-separated)
        ".edges",    // Edge list
        ".edge",     // Edge list
        ".el",       // Edge list
        ".txt",      // Edge list (fallback)
    ];

    /// <summary>
    /// Glob pattern matching all supported extensions, for directory scanning.
    /// </summary>
    public static IEnumerable<string> FindGraphFiles(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        foreach (var ext in SupportedExtensions)
        {
            foreach (var file in Directory.GetFiles(directory, $"*{ext}", SearchOption.AllDirectories))
                yield return file;
        }
    }

    /// <summary>
    /// Load a graph file, auto-detecting format from the file extension.
    /// </summary>
    public static GraphModel ReadFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mtx" => MatrixMarketReader.ReadFile(path),
            ".gml" => GmlReader.ReadFile(path),
            ".graphml" => GraphMlReader.ReadFile(path),
            ".csv" or ".tsv" or ".edges" or ".edge" or ".el" or ".txt"
                => EdgeListReader.ReadFile(path),
            _ => throw new NotSupportedException(
                $"Unsupported graph file format: '{ext}'. " +
                $"Supported: {string.Join(", ", SupportedExtensions)}")
        };
    }
}
