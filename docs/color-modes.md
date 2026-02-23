# Color Modes for Graph Visualization

## Philosophy

In data visualization, color is a channel of information. Each color mode
answers a different question about the same graph. The graph structure stays
fixed; only the visual encoding changes. This lets the user explore multiple
facets of the data without reloading or relaying out.

---

## Vertex Coloring Modes

### Structural / Topological

| Mode | What it reveals | Color scheme | Complexity |
|------|----------------|--------------|------------|
| **Community** (current default) | Modularity-based clusters via label propagation | Categorical palette | O(n) lookup |
| **Coarsening parent** | Which fine nodes merge into the same coarse super-node | Parent's community color, vary brightness | O(n) lookup |
| **Connected component** | Disconnected subgraphs — instantly shows islands | Categorical | O(n+m) BFS/DFS |
| **Degree centrality** | Hubs vs. leaves — degree mapped to gradient | Sequential cool→hot | O(1) per node (degree precomputed) |
| **Betweenness centrality** | Bottleneck / bridge nodes on many shortest paths | Sequential | O(n·m) — expensive |
| **PageRank** (directed) | Influence/authority in citation/web/dependency graphs | Sequential | O(n·k) iterative |
| **In/out degree ratio** (directed) | Sources vs. sinks vs. balanced nodes | Diverging blue→white→red | O(n) scan of edges |
| **Local clustering coefficient** | Tightly-knit vs. loosely-connected neighborhoods | Sequential | O(n·d²) |
| **K-core shell** | Hierarchical density decomposition ("onion layers") | Sequential by shell number | O(n+m) |
| **Depth from selection** | BFS/Dijkstra distance from selected node(s) | Sequential heatmap "ripple" | O(n+m) BFS |

### Data-driven / Attribute

| Mode | What it reveals | Color scheme |
|------|----------------|--------------|
| **Label/category** | Input-provided grouping (political alignment, etc.) | Categorical |
| **Numeric attribute** | Any scalar property from data (weight, timestamp) | Sequential or diverging |
| **Anomaly / outlier** | Structurally-deviant nodes | Binary accent overlay |
| **Search / filter match** | Nodes matching text query | Saturated highlight, rest dimmed |

---

## Edge Coloring Modes

| Mode | What it reveals | Color scheme |
|------|----------------|--------------|
| **Weight / strength** | Strong vs. weak ties | Sequential gradient + thickness |
| **Intra- vs. inter-community** | Within-cluster vs. bridge edges | Community color (saturated) vs. neutral gray |
| **Reciprocity** (directed) | Mutual edges (A→B ∧ B→A) vs. one-way | Two distinct hues |
| **Edge betweenness** | Edges on many shortest paths ("highways") | Sequential |
| **Bridge / cut edge** | Removal would disconnect the graph | Bright accent |
| **Spanning tree backbone** | Minimal connected structure | Solid vs. faded/dashed |
| **Shortest path highlight** | Path between two selected nodes | Bright overlay |
| **Source→target gradient** (directed) | Direction of flow | Gradient along edge |

---

## Implementation Architecture

### Parallel Color Arrays

Color data stored as parallel arrays alongside GraphModel's existing node/edge
arrays. Each color mode produces an `Rgba32[]` array of length `NodeCount` (or
`EdgeCount` for edge modes). Arrays are computed on-demand and cached.

```
NodeColors["community"]  → Rgba32[NodeCount]   // categorical
NodeColors["degree"]     → Rgba32[NodeCount]   // sequential gradient
NodeColors["in_out"]     → Rgba32[NodeCount]   // diverging gradient

EdgeColors["community"]  → Rgba32[EdgeCount]   // intra vs inter
```

### Rgba32 Struct

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct Rgba32(byte r, byte g, byte b, byte a = 255)
{
    public readonly byte R = r, G = g, B = b, A = a;
}
```

Lightweight, blittable, no GC pressure. Convertible to `System.Drawing.Color`
or `SkiaSharp.SKColor` at render time.

### On-Demand Computation & Caching

`NodeColorProvider` holds a `Dictionary<string, Rgba32[]>` cache. When the
color mode is set, it checks the cache. If the array exists, it reuses it.
If the graph changes (SetGraph/SetGraphWithHierarchy), the cache is invalidated.

### Render Path

In `OnPaint`, instead of:
```csharp
fillPaint.Color = SkPalette[community[i] % SkPalette.Length];
```

The code becomes:
```csharp
var c = _nodeColors[i];
fillPaint.Color = new SKColor(c.R, c.G, c.B, c.A);
```

A single `Rgba32[]` reference is grabbed at the start of the render frame,
so the per-node cost is one array index instead of a modulo + palette lookup.

---

## Priority Implementation (Top 3)

### 1. Community (existing, extracted into framework)
- Already implemented via `GraphModel.Community[]` + palette modulo
- Extracted into the `NodeColorProvider` as the `community` mode
- Serves as the baseline / default

### 2. Degree Centrality
- `degree[i] = AdjOffset[i+1] - AdjOffset[i]` — already computed
- Map to a Viridis-like sequential gradient: low-degree cool, high-degree hot
- Instantly reveals hub structure

### 3. In/Out Degree Ratio (directed graphs)
- Compute `inDeg[i]` and `outDeg[i]` from EdgeSource/EdgeTarget arrays
- Map `(outDeg - inDeg) / (outDeg + inDeg)` to a diverging colormap
  - Blue (-1) = pure sink (all incoming)
  - White (0) = balanced
  - Red (+1) = pure source (all outgoing)
- Falls back to degree centrality for undirected graphs
- Makes the wiki-vote graph tell a story about voter influence

### + Edge community coloring (bonus)
- Intra-community edges: tinted with the community color at low alpha
- Inter-community edges: neutral gray
- Dramatically clarifies cluster boundaries
