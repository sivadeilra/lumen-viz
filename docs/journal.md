# LumenViz Development Journal

Most recent entries first.

---

## 2025-02-23 — Graph Format Support & Architecture Refactoring

### Context

LumenViz currently loads only Matrix Market (.mtx) files. This limits us to
datasets from the SuiteSparse Matrix Collection. To work with a wider range of
graphs — including directed, DAG, and cyclic directed graphs — we need support
for the most common exchange formats.

Additionally, the entire codebase (graph model, layout algorithms, file readers,
MCP server, and WinForms GUI) lives in a single project. This prevents reuse
of the graph engine in non-visual contexts.

### File Format Research

Surveyed formats used by NetworkX, igraph, Gephi, yEd, Graphviz, and the
algorithmic competition community. Ranked by ubiquity and practical importance:

**Tier 1 — High priority:**

| Format | Extension(s) | Directed support | Complexity |
|--------|-------------|-----------------|------------|
| Edge List | .csv, .tsv, .edges, .txt | By convention | Trivial |
| GML | .gml | `directed 0/1` flag | Easy–Medium |
| GraphML | .graphml | `edgedefault` attr, per-edge override | Easy (XML) |
| DOT (Graphviz) | .gv, .dot | `graph` vs `digraph` keyword | Medium |

**Tier 2 — Worth having later:**

| Format | Extension(s) | Notes |
|--------|-------------|-------|
| GEXF | .gexf | Gephi native, XML |
| Pajek | .net | SNA standard, simple text |
| JSON node-link | .json | D3.js ecosystem |
| DIMACS | .col, .dimacs | Algorithm challenges |

### Directed Graph Support

Current state: `GraphModel` is always undirected. `BuildAdjacency()` inserts
both directions for every edge. `MatrixMarketReader` normalizes edges to
`(min, max)` even for non-symmetric (general) matrices, losing directionality.

Plan: Add `IsDirected` property to `GraphModel`. For directed graphs:
- `EdgeSource`/`EdgeTarget` preserve original direction
- CSR adjacency remains bidirectional (layout algorithms need it)
- Rendering draws arrows for directed edges (future visual work)
- Reader dedup uses ordered pair for directed, unordered for undirected

### Architecture Refactoring Plan

**Split into two projects:**

1. **LumenGraph** (class library DLL, `net9.0`)
   - `GraphModel` — core data structure
   - All readers: `MatrixMarketReader`, `EdgeListReader`, `GmlReader`, `GraphMlReader`
   - `GraphReader` — format dispatch (extension-based auto-detection)
   - Layout algorithms: `ForceLayout`, `QuadTree`, `MultiLevelLayout`
   - Coarsening: `Coarsening`, `CoarseLevel`, `CoarseningHierarchy`
   - Community detection (already in GraphModel)
   - Zero UI dependencies — targets plain `net9.0`

2. **LumenViz** (WinForms + MCP, `net9.0-windows`)
   - `McpServer` — JSON-RPC 2.0 MCP server
   - `VizWindow`, `GraphView`, `SkiaGraphView` — GUI rendering
   - `MainForm`, `Program` — app entry points
   - References `LumenGraph` DLL

**MCP Architecture Decision: Single MCP recommended.**

Rationale for one MCP with both visual and non-visual tools:
- Tooling overhead: users register one MCP server, not two
- Shared state: analysis results can feed directly into visualization
- Natural workflow: "analyze this graph" → "now show me the result"
- Implementation: `--mcp` flag already runs headless; visual tools create
  windows on demand. Non-visual analysis tools simply never create windows.
- The LumenGraph DLL provides the reusable engine; the single MCP is the
  unified interface that exposes both analysis and visualization capabilities.

The alternative (two MCPs) would require IPC to share graph state between
analysis and visualization, adding complexity for no clear benefit.

### Implementation Order

1. Create `LumenGraph` class library + solution file
2. Move graph core files into `LumenGraph`
3. Add `IsDirected` to `GraphModel`, fix `MatrixMarketReader`
4. Implement `EdgeListReader`, `GmlReader`, `GraphMlReader`
5. Add `GraphReader` dispatch layer
6. Download example graphs for each format
7. Update MCP `load_graph` and `list_data_files`
8. Add non-visual graph analysis MCP tools (future)

### Experiment Results Reference

The Skia vs GDI+ experiment (documented in `docs/skia-vs-gdi-experiment.md`)
showed 3.1× total speedup with the Skia software rasterizer, informing the
decision to keep SkiaSharp as a LumenViz-only dependency (not in LumenGraph).
