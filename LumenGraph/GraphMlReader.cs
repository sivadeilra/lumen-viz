using System.Globalization;
using System.Xml.Linq;

namespace LumenGraph;

/// <summary>
/// Reads GraphML (.graphml) files — an XML-based graph exchange format.
///
/// Supports:
///   - Directed and undirected graphs (edgedefault attribute)
///   - Per-edge direction override (directed="true|false")
///   - Node labels (via data keys or id fallback)
///   - Edge weights (via data keys named "weight", "value", or "d1")
///   - Multiple graphs per file (reads the first one)
///
/// Used by yEd, Gephi, NetworkX, igraph, Boost Graph Library.
/// Extension: .graphml
/// </summary>
public static class GraphMlReader
{
    private static readonly XNamespace Ns = "http://graphml.graphdrawing.org/xmlns";

    public static GraphModel ReadFile(string path)
    {
        var doc = XDocument.Load(path);
        return Read(doc, Path.GetFileNameWithoutExtension(path));
    }

    public static GraphModel Read(XDocument doc, string title = "")
    {
        var graph = new GraphModel { Title = title };

        var root = doc.Root;
        if (root == null)
            throw new FormatException("Empty GraphML document");

        // Find the <graph> element (with or without namespace)
        var graphElem = root.Element(Ns + "graph") ?? root.Element("graph");
        if (graphElem == null)
            throw new FormatException("No <graph> element found in GraphML file");

        // Determine default edge direction
        string edgeDefault = (string?)graphElem.Attribute("edgedefault") ?? "undirected";
        bool defaultDirected = edgeDefault.Equals("directed", StringComparison.OrdinalIgnoreCase);
        graph.IsDirected = defaultDirected;

        // Discover data key names for labels and weights
        string? labelKey = null;
        string? weightKey = null;
        foreach (var keyElem in root.Elements(Ns + "key").Concat(root.Elements("key")))
        {
            string? attrName = (string?)keyElem.Attribute("attr.name");
            string? id = (string?)keyElem.Attribute("id");
            string? forAttr = (string?)keyElem.Attribute("for");

            if (attrName != null)
            {
                string lower = attrName.ToLowerInvariant();
                if (forAttr == "node" && (lower == "label" || lower == "name") && labelKey == null)
                    labelKey = id;
                if (forAttr == "edge" && (lower == "weight" || lower == "value") && weightKey == null)
                    weightKey = id;
            }
        }

        // Parse nodes
        var nodeMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeLabels = new List<string>();

        foreach (var nodeElem in graphElem.Elements(Ns + "node").Concat(graphElem.Elements("node")))
        {
            string? id = (string?)nodeElem.Attribute("id");
            if (id == null) continue;

            int idx = nodeMap.Count;
            nodeMap[id] = idx;

            // Try to get label from data element
            string label = id;
            if (labelKey != null)
            {
                var dataElem = FindData(nodeElem, labelKey);
                if (dataElem != null)
                    label = dataElem.Value.Trim();
            }
            // Also check for a "label" attribute directly on the node
            string? labelAttr = (string?)nodeElem.Attribute("label");
            if (labelAttr != null)
                label = labelAttr;

            nodeLabels.Add(label);
        }

        // Parse edges
        var edgeSet = new HashSet<(int, int)>();
        var tmpSrc = new List<int>();
        var tmpTgt = new List<int>();
        var tmpWgt = new List<double>();

        foreach (var edgeElem in graphElem.Elements(Ns + "edge").Concat(graphElem.Elements("edge")))
        {
            string? source = (string?)edgeElem.Attribute("source");
            string? target = (string?)edgeElem.Attribute("target");
            if (source == null || target == null) continue;

            if (!nodeMap.TryGetValue(source, out int s) ||
                !nodeMap.TryGetValue(target, out int t))
                continue;

            if (s == t) continue; // skip self-loops

            // Per-edge direction override
            string? dirAttr = (string?)edgeElem.Attribute("directed");
            bool edgeDirected = dirAttr != null
                ? dirAttr.Equals("true", StringComparison.OrdinalIgnoreCase)
                : defaultDirected;

            // Weight
            double weight = 1.0;
            if (weightKey != null)
            {
                var dataElem = FindData(edgeElem, weightKey);
                if (dataElem != null)
                    double.TryParse(dataElem.Value.Trim(), CultureInfo.InvariantCulture, out weight);
            }

            if (edgeDirected)
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

        // Build graph model
        graph.AllocNodes(nodeMap.Count);
        for (int i = 0; i < nodeLabels.Count; i++)
            graph.Labels[i] = nodeLabels[i];

        graph.AllocEdges(tmpSrc.Count);
        tmpSrc.CopyTo(graph.EdgeSource);
        tmpTgt.CopyTo(graph.EdgeTarget);
        tmpWgt.CopyTo(graph.EdgeWeight);

        graph.BuildAdjacency();
        return graph;
    }

    private static XElement? FindData(XElement parent, string key)
    {
        foreach (var d in parent.Elements(Ns + "data").Concat(parent.Elements("data")))
        {
            if ((string?)d.Attribute("key") == key)
                return d;
        }
        return null;
    }
}
