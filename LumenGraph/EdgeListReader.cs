using System.Globalization;

namespace LumenGraph;

/// <summary>
/// Reads simple edge list files. Each non-comment line contains one edge:
///   source  target  [weight]
///
/// Fields are separated by whitespace, comma, semicolon, or tab.
/// Lines starting with '#', '%', or '//' are comments.
///
/// The first comment line may contain a directive:
///   # directed
///   # undirected
/// If absent, the graph defaults to undirected.
///
/// Node IDs can be integers (0-based or 1-based) or strings.
/// String IDs are mapped to sequential integers automatically.
///
/// Common extensions: .csv, .tsv, .edges, .txt, .edge, .el
/// </summary>
public static class EdgeListReader
{
    private static readonly char[] Separators = [' ', '\t', ',', ';'];

    public static GraphModel ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return Read(reader, Path.GetFileNameWithoutExtension(path));
    }

    public static GraphModel Read(TextReader reader, string title = "")
    {
        var graph = new GraphModel { Title = title };
        bool? directedHint = null;

        var nodeMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var tmpSrc = new List<int>();
        var tmpTgt = new List<int>();
        var tmpWgt = new List<double>();
        var edgeSet = new HashSet<(int, int)>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            // Comment lines
            if (line[0] == '#' || line[0] == '%' || line.StartsWith("//"))
            {
                // Check for directed/undirected directive
                string lower = line.ToLowerInvariant();
                if (lower.Contains("directed") && !lower.Contains("undirected"))
                    directedHint = true;
                else if (lower.Contains("undirected"))
                    directedHint = false;
                continue;
            }

            var parts = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            int src = GetOrAddNode(nodeMap, parts[0]);
            int tgt = GetOrAddNode(nodeMap, parts[1]);

            double weight = 1.0;
            if (parts.Length > 2)
                double.TryParse(parts[2], CultureInfo.InvariantCulture, out weight);

            // Skip self-loops
            if (src == tgt) continue;

            bool isDirected = directedHint ?? false;

            if (isDirected)
            {
                if (edgeSet.Add((src, tgt)))
                {
                    tmpSrc.Add(src);
                    tmpTgt.Add(tgt);
                    tmpWgt.Add(weight);
                }
            }
            else
            {
                int lo = Math.Min(src, tgt);
                int hi = Math.Max(src, tgt);
                if (edgeSet.Add((lo, hi)))
                {
                    tmpSrc.Add(lo);
                    tmpTgt.Add(hi);
                    tmpWgt.Add(weight);
                }
            }
        }

        graph.IsDirected = directedHint ?? false;

        // Allocate nodes and set labels
        int nodeCount = nodeMap.Count;
        graph.AllocNodes(nodeCount);

        // If any node ID was non-numeric, use original string labels
        foreach (var (label, idx) in nodeMap)
            graph.Labels[idx] = label;

        // Write edges
        graph.AllocEdges(tmpSrc.Count);
        tmpSrc.CopyTo(graph.EdgeSource);
        tmpTgt.CopyTo(graph.EdgeTarget);
        tmpWgt.CopyTo(graph.EdgeWeight);

        graph.BuildAdjacency();
        return graph;
    }

    private static int GetOrAddNode(Dictionary<string, int> map, string id)
    {
        if (!map.TryGetValue(id, out int idx))
        {
            idx = map.Count;
            map[id] = idx;
        }
        return idx;
    }
}
