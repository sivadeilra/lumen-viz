using System.Globalization;
using System.Text;

namespace LumenGraph;

/// <summary>
/// Reads Graph Modelling Language (GML) files.
///
/// GML uses a hierarchical bracket syntax:
///   graph [
///     directed 0|1
///     node [ id N  label "..." ]
///     edge [ source N  target N  weight W ]
///   ]
///
/// Supports: directed/undirected flag, node id/label, edge source/target/weight,
/// nested attributes (skipped). Used by igraph, NetworkX, Gephi, yEd.
///
/// Extension: .gml
/// </summary>
public static class GmlReader
{
    public static GraphModel ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return Read(reader, Path.GetFileNameWithoutExtension(path));
    }

    public static GraphModel Read(TextReader reader, string title = "")
    {
        var tokens = Tokenize(reader);
        var graph = new GraphModel { Title = title };

        // Parse the top-level "graph [ ... ]"
        int pos = 0;
        while (pos < tokens.Count)
        {
            if (tokens[pos] == "graph" && pos + 1 < tokens.Count && tokens[pos + 1] == "[")
            {
                pos += 2; // skip "graph" and "["
                ParseGraphBlock(tokens, ref pos, graph);
                break;
            }
            pos++;
        }

        return graph;
    }

    private static void ParseGraphBlock(List<string> tokens, ref int pos, GraphModel graph)
    {
        bool isDirected = false;
        var nodes = new List<(int id, string label)>();
        var edges = new List<(int source, int target, double weight)>();

        while (pos < tokens.Count && tokens[pos] != "]")
        {
            string key = tokens[pos];

            if (key == "directed" && pos + 1 < tokens.Count)
            {
                pos++;
                isDirected = tokens[pos] == "1";
                pos++;
            }
            else if (key == "node" && pos + 1 < tokens.Count && tokens[pos + 1] == "[")
            {
                pos += 2; // skip "node" and "["
                var (id, label) = ParseNodeBlock(tokens, ref pos);
                nodes.Add((id, label));
            }
            else if (key == "edge" && pos + 1 < tokens.Count && tokens[pos + 1] == "[")
            {
                pos += 2; // skip "edge" and "["
                var (source, target, weight) = ParseEdgeBlock(tokens, ref pos);
                edges.Add((source, target, weight));
            }
            else
            {
                pos++;
                // Skip nested blocks we don't care about
                if (pos < tokens.Count && tokens[pos] == "[")
                {
                    pos++;
                    SkipBlock(tokens, ref pos);
                }
            }
        }

        if (pos < tokens.Count) pos++; // skip closing "]"

        graph.IsDirected = isDirected;

        // Map GML node IDs (which can be arbitrary ints) to 0-based indices
        var idToIndex = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            idToIndex[nodes[i].id] = i;

        graph.AllocNodes(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            graph.Labels[i] = nodes[i].label;

        // Build edges, mapping GML IDs to indices
        var edgeSet = new HashSet<(int, int)>();
        var tmpSrc = new List<int>();
        var tmpTgt = new List<int>();
        var tmpWgt = new List<double>();

        foreach (var (source, target, weight) in edges)
        {
            if (!idToIndex.TryGetValue(source, out int s) ||
                !idToIndex.TryGetValue(target, out int t))
                continue; // skip edges referencing unknown nodes

            if (s == t) continue; // skip self-loops

            if (isDirected)
            {
                if (edgeSet.Add((s, t)))
                {
                    tmpSrc.Add(s);
                    tmpTgt.Add(t);
                    tmpWgt.Add(weight);
                }
            }
            else
            {
                int lo = Math.Min(s, t);
                int hi = Math.Max(s, t);
                if (edgeSet.Add((lo, hi)))
                {
                    tmpSrc.Add(lo);
                    tmpTgt.Add(hi);
                    tmpWgt.Add(weight);
                }
            }
        }

        graph.AllocEdges(tmpSrc.Count);
        tmpSrc.CopyTo(graph.EdgeSource);
        tmpTgt.CopyTo(graph.EdgeTarget);
        tmpWgt.CopyTo(graph.EdgeWeight);

        graph.BuildAdjacency();
    }

    private static (int id, string label) ParseNodeBlock(List<string> tokens, ref int pos)
    {
        int id = -1;
        string label = "";

        while (pos < tokens.Count && tokens[pos] != "]")
        {
            string key = tokens[pos];
            if (key == "id" && pos + 1 < tokens.Count)
            {
                pos++;
                int.TryParse(tokens[pos], out id);
                pos++;
            }
            else if (key == "label" && pos + 1 < tokens.Count)
            {
                pos++;
                label = Unquote(tokens[pos]);
                pos++;
            }
            else
            {
                pos++;
                if (pos < tokens.Count && tokens[pos] == "[")
                {
                    pos++;
                    SkipBlock(tokens, ref pos);
                }
            }
        }

        if (pos < tokens.Count) pos++; // skip "]"

        if (label == "") label = id.ToString();
        return (id, label);
    }

    private static (int source, int target, double weight) ParseEdgeBlock(List<string> tokens, ref int pos)
    {
        int source = -1, target = -1;
        double weight = 1.0;

        while (pos < tokens.Count && tokens[pos] != "]")
        {
            string key = tokens[pos];
            if (key == "source" && pos + 1 < tokens.Count)
            {
                pos++;
                int.TryParse(tokens[pos], out source);
                pos++;
            }
            else if (key == "target" && pos + 1 < tokens.Count)
            {
                pos++;
                int.TryParse(tokens[pos], out target);
                pos++;
            }
            else if ((key == "weight" || key == "value") && pos + 1 < tokens.Count)
            {
                pos++;
                double.TryParse(tokens[pos], CultureInfo.InvariantCulture, out weight);
                pos++;
            }
            else
            {
                pos++;
                if (pos < tokens.Count && tokens[pos] == "[")
                {
                    pos++;
                    SkipBlock(tokens, ref pos);
                }
            }
        }

        if (pos < tokens.Count) pos++; // skip "]"
        return (source, target, weight);
    }

    private static void SkipBlock(List<string> tokens, ref int pos)
    {
        int depth = 1;
        while (pos < tokens.Count && depth > 0)
        {
            if (tokens[pos] == "[") depth++;
            else if (tokens[pos] == "]") depth--;
            pos++;
        }
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    /// <summary>
    /// Tokenize GML text into a flat list of tokens.
    /// Tokens: identifiers, numbers, "[", "]", quoted strings.
    /// </summary>
    private static List<string> Tokenize(TextReader reader)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();

        int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;

            // Skip whitespace
            if (char.IsWhiteSpace(ch)) continue;

            // Skip comments (# to end of line)
            if (ch == '#')
            {
                reader.ReadLine();
                continue;
            }

            // Brackets
            if (ch == '[' || ch == ']')
            {
                tokens.Add(ch.ToString());
                continue;
            }

            // Quoted string
            if (ch == '"')
            {
                sb.Clear();
                sb.Append('"');
                while ((c = reader.Read()) >= 0)
                {
                    char qc = (char)c;
                    sb.Append(qc);
                    if (qc == '"') break;
                    if (qc == '\\')
                    {
                        int nc = reader.Read();
                        if (nc >= 0) sb.Append((char)nc);
                    }
                }
                tokens.Add(sb.ToString());
                continue;
            }

            // Identifier or number: read until whitespace/bracket/quote
            sb.Clear();
            sb.Append(ch);
            while ((c = reader.Peek()) >= 0)
            {
                char nc = (char)c;
                if (char.IsWhiteSpace(nc) || nc == '[' || nc == ']' || nc == '"')
                    break;
                sb.Append(nc);
                reader.Read();
            }
            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
