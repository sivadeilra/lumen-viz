namespace LumenViz;

/// <summary>
/// Lightweight graph data model — nodes with 2D positions, edges with
/// optional weights. Designed for sparse graph visualization.
/// </summary>
public class GraphModel
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public string Title { get; set; } = "";

    public GraphNode AddNode(string? label = null)
    {
        var node = new GraphNode
        {
            Index = Nodes.Count,
            Label = label ?? Nodes.Count.ToString(),
        };
        Nodes.Add(node);
        return node;
    }

    public GraphEdge AddEdge(int source, int target, double weight = 1.0)
    {
        var edge = new GraphEdge
        {
            Source = source,
            Target = target,
            Weight = weight,
        };
        Edges.Add(edge);
        return edge;
    }

    /// <summary>
    /// Build adjacency lists from the edge list, for fast neighbor traversal.
    /// Call this after all edges have been added.
    /// </summary>
    public void BuildAdjacency()
    {
        foreach (var node in Nodes)
            node.Neighbors.Clear();

        foreach (var edge in Edges)
        {
            Nodes[edge.Source].Neighbors.Add(edge.Target);
            Nodes[edge.Target].Neighbors.Add(edge.Source);
        }
    }

    /// <summary>
    /// Assign a community/group index to each node using a greedy modularity
    /// algorithm (label propagation). Simple but effective for visualization
    /// coloring.
    /// </summary>
    public void DetectCommunities()
    {
        // Initialize each node in its own community
        foreach (var node in Nodes)
            node.Community = node.Index;

        // Label propagation: repeatedly adopt the most common neighbor label
        var rng = new Random(42);
        var order = Enumerable.Range(0, Nodes.Count).ToArray();

        for (int iter = 0; iter < 50; iter++)
        {
            bool changed = false;
            rng.Shuffle(order);

            foreach (int i in order)
            {
                var node = Nodes[i];
                if (node.Neighbors.Count == 0) continue;

                // Count neighbor communities
                var counts = new Dictionary<int, int>();
                foreach (int nb in node.Neighbors)
                {
                    var c = Nodes[nb].Community;
                    counts.TryGetValue(c, out int count);
                    counts[c] = count + 1;
                }

                // Pick the most common
                int bestLabel = node.Community;
                int bestCount = 0;
                foreach (var (label, count) in counts)
                {
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestLabel = label;
                    }
                }

                if (bestLabel != node.Community)
                {
                    node.Community = bestLabel;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        // Renumber communities to 0..K-1
        var communityMap = new Dictionary<int, int>();
        foreach (var node in Nodes)
        {
            if (!communityMap.ContainsKey(node.Community))
                communityMap[node.Community] = communityMap.Count;
            node.Community = communityMap[node.Community];
        }
    }
}

public class GraphNode
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public int Community { get; set; }
    public List<int> Neighbors { get; } = new();
}

public class GraphEdge
{
    public int Source { get; set; }
    public int Target { get; set; }
    public double Weight { get; set; } = 1.0;
}
