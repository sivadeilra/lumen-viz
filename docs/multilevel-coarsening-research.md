# Multi-Level Graph Coarsening for Force-Directed Layout

*Research summary — February 2026*
*Target: LumenViz (C# / Fruchterman-Reingold)*

---

## 1. Overview: What Is Multi-Level Graph Coarsening?

Force-directed layout algorithms like Fruchterman-Reingold (FR) have an O(n²) per-iteration cost for repulsive forces, and more critically, they converge to **local minima** on large graphs. Nodes that are graph-theoretically far apart may never "discover" each other through local force interactions — the layout gets stuck in tangled hairballs.

**Multi-level coarsening** solves both problems by working on progressively smaller approximations of the graph:

### The Three-Phase Framework

```
Phase 1: COARSEN                Phase 2: LAYOUT          Phase 3: UNCOARSEN (Refine)
┌──────────────┐               ┌──────────────┐         ┌──────────────┐
│ G₀ (original)│               │              │         │ G₀ refined   │
│  n=10000     │──▶            │              │    ◀──  │  n=10000     │
│              │               │              │         │  (final)     │
├──────────────┤               │              │         ├──────────────┤
│ G₁ coarsened │──▶            │              │    ◀──  │ G₁ refined   │
│  n=3000      │               │              │         │  n=3000      │
├──────────────┤               │              │         ├──────────────┤
│ G₂ coarsened │──▶            │              │    ◀──  │ G₂ refined   │
│  n=800       │               │              │         │  n=800       │
├──────────────┤               │              │         ├──────────────┤
│ G₃ coarsened │──▶ FR layout  │ G₃ laid out  │──▶      │ G₃ (seed)   │
│  n=150       │   (cheap!)    │  n=150       │         │  n=150       │
└──────────────┘               └──────────────┘         └──────────────┘
```

**Phase 1 — Coarsen:** Repeatedly contract the graph by merging groups of nodes into single "super-nodes." Each level roughly halves the node count. Edge weights between super-nodes are summed. Stop when the graph is small enough for direct layout (typically 50–200 nodes).

**Phase 2 — Initial Layout:** Run a force-directed algorithm on the coarsest graph. This is cheap (small graph) and finds a good global structure because the coarse graph captures the large-scale topology.

**Phase 3 — Uncoarsen/Refine:** Expand each level back, placing child nodes near their parent super-node's position. After each expansion, run a few FR iterations to refine local placement. The global structure is preserved from the coarse layout; local refinement handles fine detail.

### Why It Works

1. **Global structure emerges first** — the coarsest graph captures the "skeleton" of the graph, so distant communities are placed correctly relative to each other
2. **Speed** — most FR iterations happen on small graphs. Refinement at each level needs only a few iterations (the initial placement from the parent is already good)
3. **Avoids local minima** — the coarse-to-fine approach naturally escapes local minima that plague flat FR
4. **Typical speedup**: 10–100× for graphs with 10K+ nodes, with *better* layout quality

---

## 2. Key Coarsening Algorithms

### 2.1 Heavy-Edge Matching (HEM) — Karypis & Kumar (METIS)

**Origin:** The METIS graph partitioner (1998). Also used in multi-level graph drawing.

**Core idea:** Find a *maximal matching* — a set of edges where no two share a vertex — preferring heavy (high-weight) edges. Matched pairs are merged into super-nodes.

**Algorithm:**

```
function HeavyEdgeMatching(G = (V, E, w)):
    matched = array[|V|] of false
    mapping = array[|V|] of -1   // maps fine node → coarse node
    coarseId = 0
    
    // Visit nodes in random order (important for quality!)
    order = RandomPermutation(V)
    
    for u in order:
        if matched[u]: continue
        
        // Find heaviest unmatched neighbor
        bestNeighbor = -1
        bestWeight = -∞
        for (u, v, w) in edges(u):
            if not matched[v] and w > bestWeight:
                bestWeight = w
                bestNeighbor = v
        
        if bestNeighbor != -1:
            // Merge u and bestNeighbor into coarse node
            matched[u] = true
            matched[bestNeighbor] = true
            mapping[u] = coarseId
            mapping[bestNeighbor] = coarseId
            coarseId++
        else:
            // Singleton — no unmatched neighbor available
            matched[u] = true
            mapping[u] = coarseId
            coarseId++
    
    // Build coarse graph
    Gc = new Graph(coarseId nodes)
    for each edge (u, v, w) in E:
        cu = mapping[u]
        cv = mapping[v]
        if cu != cv:
            Gc.AddOrMergeEdge(cu, cv, w)  // sum weights if edge exists
        // else: internal edge, contributes to super-node weight
    
    return (Gc, mapping)
```

**Properties:**
- **Coarsening ratio:** ~2:1 per level (each matching eliminates roughly half the nodes)
- **Time:** O(|E|) per level — just one pass through edges
- **Space:** O(|V| + |E|) for the mapping + coarse graph
- **Levels needed:** log₂(n) to reach ~constant size
- **Total time:** O(|E| log n) for full coarsening hierarchy

**Variants:**
- **Random matching (RM):** Pick any unmatched neighbor (simpler, slightly worse)
- **Sorted heavy-edge matching (SHEM):** Sort edges by weight, process heaviest first. Better quality, O(|E| log |E|) per level
- **Light-edge matching:** Prefer light edges (collapses tightly-connected clusters last). Used in some partitioners

**Strengths:** Simple, fast, proven. This is the workhorse algorithm.

---

### 2.2 Edge Collapsing — Walshaw (JOSTLE)

**Origin:** Walshaw (2000, 2003). Used in the multi-level graph drawing framework JOSTLE.

**Core idea:** Similar to HEM, but specifically designed for graph drawing rather than partitioning. Key differences:

1. **Edges are prioritized by a drawing-quality metric** — shorter edges in the current layout, or edges between high-degree nodes (which benefit most from coarsening)
2. **Maximum independent set (MIS) based selection** — instead of matching, select an independent set of nodes and collapse their neighborhoods

**Walshaw's MIS-based approach:**

```
function MIS_Coarsen(G = (V, E)):
    // Select a maximal independent set (2-independent set)
    // Nodes at graph-distance ≥ 2 from each other
    S = []
    removed = set()
    
    // Sort by degree ascending (low-degree nodes are "centers")
    for u in V sorted by degree ascending:
        if u in removed: continue
        S.add(u)
        removed.add(u)
        for v in neighbors(u):
            removed.add(v)
    
    // Each non-center node is assigned to its nearest center
    mapping = array[|V|]
    for u in S:
        mapping[u] = indexOf(u in S)
    
    for v in V \ S:
        // Assign to neighbor in S with heaviest connecting edge
        bestCenter = argmax over (u in neighbors(v) ∩ S) of weight(v, u)
        mapping[v] = indexOf(bestCenter in S)
    
    // Build coarse graph (same as HEM)
    return BuildCoarseGraph(G, mapping, |S|)
```

**Properties:**
- Coarsening ratio: 3:1 to 5:1 (more aggressive than matching)
- Better at preserving graph structure for layout
- Used in Walshaw's multi-level FR implementation

---

### 2.3 Algebraic Multigrid (AMG) Approaches

These borrow ideas from numerical multigrid methods for solving linear systems.

#### 2.3.1 ACE — Algebraic multigrid Computation of Eigenvectors (Koren et al., 2002)

**Core idea:** Compute the graph Laplacian's eigenvectors using algebraic multigrid. The eigenvectors give optimal 2D coordinates (spectral layout). AMG coarsening is used to speed up eigenvector computation, not as a graph simplification per se.

**How it works:**
1. Form the graph Laplacian L = D - A (D = degree matrix, A = adjacency)
2. Use AMG to create a hierarchy of coarser Laplacians
3. Solve for the Fiedler vector (2nd smallest eigenvector) and 3rd eigenvector on the coarsest level
4. Interpolate up through the hierarchy, improving via relaxation at each level
5. The two eigenvectors become (x, y) coordinates

**AMG coarsening for the Laplacian:**
- Nodes are classified as C-points (coarse) or F-points (fine)
- C/F splitting uses **strength of connection**: nodes i,j are strongly connected if -a_ij ≥ θ · max_k(-a_ik) where θ ≈ 0.25
- Each F-point must be strongly connected to at least one C-point
- Interpolation weights come from the matrix entries

**Practical note:** ACE produces spectral layouts (often quite good for showing clusters) but doesn't integrate naturally with force-directed methods.

#### 2.3.2 FADE — Fast Adaptive Decomposition of Eigenvectors (Quigley & Eades, 2000)

Similar to ACE but uses a simpler coarsening based on node decimation (remove every other node in BFS order) and approximate eigenvector computation.

---

### 2.4 Solar/Merger Approach — Harel & Koren (2002)

**Origin:** Multi-scale graph drawing framework by Harel & Koren.

**Core idea:** Build a hierarchy of node subsets by selecting "solar systems" — each coarse node (sun) represents itself and nearby fine nodes (planets). The key insight is using **shortest-path distance** (not just adjacency) for grouping.

**Algorithm (k-neighborhood coarsening):**

```
function SolarCoarsen(G, k):
    // k = radius of each solar system (typically 1 or 2)
    
    centers = []      // "suns"
    assigned = set()
    
    // BFS-based center selection
    // Process nodes in random order
    for u in RandomPermutation(V):
        if u in assigned: continue
        centers.add(u)
        assigned.add(u)
        
        // BFS out to distance k
        for v in BFS(G, u, maxDepth=k):
            if v not in assigned:
                assigned.add(v)
                // v is a "planet" of sun u
    
    // Map each planet to its sun
    mapping: planet → sun's coarse index
    
    // Build coarse graph:
    // edge between coarse nodes i,j if any fine node in system i
    // is adjacent to any fine node in system j
    // edge weight = number of such cross-edges (or sum of weights)
    
    return (Gc, mapping)
```

**Used in:** The multi-scale graph drawing method by Harel & Koren, which uses Kamada-Kawai (stress minimization) at each level rather than FR.

**Properties:**
- Coarsening ratio depends on k: higher k → more aggressive coarsening
- Preserves topological distance structure well
- More expensive than HEM: O(k · |E|) per level due to BFS
- Good for stress-based layouts (Kamada-Kawai), less commonly used with FR

---

### 2.5 Label Propagation Coarsening

**Core idea:** Use label propagation (community detection) to identify natural clusters, then merge each cluster into a super-node. This is essentially what your existing `DetectCommunities()` does, repurposed for coarsening.

**Algorithm:**

```
function LabelPropagationCoarsen(G, maxClusterSize):
    // Run label propagation (same as community detection)
    label = array[|V|]
    for i in V: label[i] = i
    
    for iter in 1..20:
        changed = false
        for u in RandomPermutation(V):
            // Count neighbor labels
            counts = {}
            for v in neighbors(u):
                counts[label[v]] += weight(u, v)
            
            bestLabel = argmax(counts)
            if bestLabel != label[u]:
                label[u] = bestLabel
                changed = true
        
        if not changed: break
    
    // Optional: split oversized clusters
    for each cluster C with |C| > maxClusterSize:
        // Sub-partition C (e.g., random split)
    
    // Build coarse graph from clusters
    mapping: node → cluster id (renumbered 0..K-1)
    Gc = BuildCoarseGraph(G, mapping, K)
    return (Gc, mapping)
```

**Properties:**
- Coarsening ratio: variable (depends on graph structure). May need tuning `maxClusterSize`
- Preserves community structure by design
- Time: O(|E|) per label propagation iteration
- Risk: can produce very unbalanced super-nodes (one huge cluster, many tiny ones)

**When to use:** When you want the coarsening to respect natural community structure. Good for visualization because communities → visual clusters.

---

### 2.6 Other Notable Approaches

#### FM Refinement (Fiduccia-Mattheyses)

Not a coarsening algorithm per se, but a **refinement** algorithm used during uncoarsening. After expanding a level and placing nodes, FM refinement swaps node assignments between partitions to improve layout quality. More relevant to graph partitioning than layout.

#### GRIP (Gajer, Goodrich & Kobourov, 2000)

Uses a **maximal independent set (MIS) filtration** as the coarsening hierarchy:

```
function GRIP_Coarsen(G):
    levels = [V]   // level 0 = all nodes
    k = 0
    while |levels[k]| > threshold:
        // Compute 2-independent set of current level
        S = MaximalIndependentSet(G restricted to levels[k])
        levels[k+1] = S
        k++
    return levels
```

- Each level is a subset of the previous (unlike merging, which creates new super-nodes)
- Layout uses steepest-descent on a stress function with only the nodes at the current level
- Very clean implementation; works well in practice

#### OpenOrd

Uses a simulated annealing + force-directed hybrid with **recursive partitioning** for coarsening. Designed for very large graphs (100K+). The coarsening is essentially label propagation with density-based cutting.

---

## 3. Which Algorithm Is Best for Force-Directed Layout?

### Practical consensus from major tools:

| Tool | Coarsening | Layout | Notes |
|------|-----------|--------|-------|
| **sfdp** (Graphviz) | HEM (METIS-style) + Barnes-Hut | Modified spring-electrical | The gold standard for large graph layout |
| **FM³** | Solar system | FR variant | Good quality, more complex |
| **GRIP** | MIS filtration | Stress minimization | Clean, but stress model |
| **OpenOrd** | Label propagation | Simulated annealing + FR | Very large graphs |
| **Walshaw** | Edge collapsing (MIS) | FR | Original multi-level FR |
| **ForceAtlas2** (Gephi) | None (uses Barnes-Hut only) | Modified FR | No coarsening! |

### **Recommendation: Heavy-Edge Matching (HEM)**

For integration with your existing Fruchterman-Reingold implementation, **HEM is the clear winner**:

1. **Simplest to implement** — ~100 lines of code for the core algorithm
2. **Predictable coarsening ratio** — ~2:1 per level, log₂(n) levels
3. **O(|E|) per level** — fast enough for interactive use
4. **Proven integration with FR** — sfdp (the most widely-used large graph layout tool) uses exactly this approach
5. **Easy to map back** — each coarse node maps to exactly 2 fine nodes (or 1 for singletons)
6. **Combines naturally with Barnes-Hut** — use BH at each level for additional speedup

The algebraic (AMG/ACE) approaches are elegant but produce spectral layouts, not force-directed. Solar-system coarsening is good but more complex to implement. Label propagation coarsening is a viable alternative but less predictable in coarsening ratio.

---

## 4. Recommended Implementation: HEM + Multi-Level FR

### 4.1 Data Structures

```csharp
/// One level of the coarsening hierarchy.
class CoarseLevel
{
    GraphModel Graph;           // The coarsened graph at this level
    int[] FineToCoarse;         // Maps fine node index → coarse node index
    List<int>[] CoarseToFine;   // Maps coarse node index → list of fine node indices
}

/// The full hierarchy.
class CoarseningHierarchy
{
    List<CoarseLevel> Levels;   // Levels[0] = original, Levels[last] = coarsest
}
```

### 4.2 Complete Pseudocode

#### Phase 1: Build Coarsening Hierarchy

```
function BuildHierarchy(G₀, minSize=50):
    hierarchy = []
    G = G₀
    
    while |G.Nodes| > minSize:
        (Gc, mapping) = HeavyEdgeMatch(G)
        
        // If we couldn't reduce significantly (< 20% reduction), stop
        if |Gc.Nodes| > 0.8 * |G.Nodes|:
            break
        
        level = new CoarseLevel {
            Graph = Gc,
            FineToCoarse = mapping,
            CoarseToFine = InvertMapping(mapping, |Gc.Nodes|)
        }
        hierarchy.add(level)
        G = Gc
    
    return hierarchy

function HeavyEdgeMatch(G):
    n = |G.Nodes|
    matched = array[n] of false
    fineToCoarse = array[n] of -1
    coarseNodeCount = 0
    
    // Random permutation — critical for quality
    order = RandomPermutation(0..n-1)
    
    for u in order:
        if matched[u]: continue
        
        // Find heaviest unmatched neighbor
        bestV = -1
        bestW = -1
        for v in G.Nodes[u].Neighbors:
            if matched[v]: continue
            w = EdgeWeight(G, u, v)
            if w > bestW:
                bestW = w
                bestV = v
        
        // Create coarse node
        fineToCoarse[u] = coarseNodeCount
        matched[u] = true
        
        if bestV != -1:
            fineToCoarse[bestV] = coarseNodeCount
            matched[bestV] = true
        
        coarseNodeCount++
    
    // Build coarse graph
    Gc = new GraphModel()
    for i in 0..coarseNodeCount-1:
        Gc.AddNode()
    
    // Add coarse edges (merge parallel edges by summing weights)
    edgeMap = Dictionary<(int,int), double>()
    for each edge (u, v, w) in G.Edges:
        cu = fineToCoarse[u]
        cv = fineToCoarse[v]
        if cu == cv: continue   // internal edge — skip
        key = (min(cu,cv), max(cu,cv))
        edgeMap[key] = edgeMap.GetOrDefault(key, 0) + w
    
    for ((cu, cv), w) in edgeMap:
        Gc.AddEdge(cu, cv, w)
    
    return (Gc, fineToCoarse)
```

#### Phase 2: Layout the Coarsest Graph

```
function LayoutCoarsest(Gc):
    layout = new ForceLayout(Gc)
    layout.Iterations = 500          // more iterations — graph is tiny, so this is cheap
    layout.Run()
```

#### Phase 3: Uncoarsen with Refinement

```
function Uncoarsen(hierarchy, G₀):
    // Start from the coarsest level (already laid out)
    
    for level in hierarchy.Reversed():
        Gfine = level's fine graph
        Gcoarse = level's coarse graph  (already has positions)
        
        // PLACEMENT: Position each fine node at its coarse parent + small jitter
        for fineNode in Gfine.Nodes:
            coarseIdx = level.FineToCoarse[fineNode.Index]
            coarseNode = Gcoarse.Nodes[coarseIdx]
            
            // Place at parent position + random jitter
            // Jitter radius ∝ ideal edge length at this level
            jitter = idealEdgeLength * 0.1
            fineNode.X = coarseNode.X + random(-jitter, +jitter)
            fineNode.Y = coarseNode.Y + random(-jitter, +jitter)
        
        // REFINEMENT: Run a few FR iterations on the fine graph
        layout = new ForceLayout(Gfine)
        layout.Iterations = RefineIterations(level)  // see below
        layout.Run()    // warm start — uses existing positions
    
    // G₀ now has final positions

function RefineIterations(level):
    // More iterations for finer (larger) levels where more adjustment is needed
    // But not too many — the placement from coarser level is already good
    // Typical: 50 iterations at each level, independent of size
    // Some implementations scale: max(10, 50 * (coarseSize / fineSize))
    return 50
```

### 4.3 How the Hierarchy Maps Back During Uncoarsening

The mapping is straightforward:

```
Level 3 (coarsest):  [C₀, C₁, C₂]          ← 3 super-nodes
                      ↕    ↕    ↕
Level 2:             [n₀n₁, n₂n₃, n₄n₅]    ← 6 nodes (each coarse node splits into 2)
                      ↕ ↕   ↕ ↕   ↕ ↕
Level 1:             [a,b, c,d, e,f,g,h]     ← 8 nodes
                      ...
Level 0 (original):  [full original graph]    ← all original nodes
```

Each `CoarseToFine[coarseIdx]` gives you the list of fine nodes that were merged. During uncoarsening, you:

1. Look up the coarse node's (x, y) position
2. Place all its fine children at that position (+ jitter to break symmetry)
3. Run refinement iterations

The jitter is important — without it, merged nodes start exactly overlapping and FR's repulsive force is undefined (0/0). A small random offset solves this.

### 4.4 Time and Space Complexity

**Coarsening phase:**
- Per level: O(|E|) for matching + O(|E|) for building coarse graph
- Number of levels: O(log n) (halving each time)
- Total: **O(|E| log n)**

**Layout phase (coarsest):**
- FR on ~50–200 nodes: O(n² · iterations) ≈ O(50² · 500) = negligible

**Uncoarsening/refinement phase:**
- At level k with nₖ nodes and eₖ edges:
  - FR iterations: typically 50
  - Per iteration (with Barnes-Hut): O(nₖ log nₖ + eₖ)
  - Per iteration (with grid approx): O(nₖ + eₖ)
  - Per iteration (naive): O(nₖ² + eₖ)
- Sum over all levels: dominated by the finest level
- With Barnes-Hut: **O(n log n + |E|) per refinement iteration**
- Total refinement: **O(50 · (n log n + |E|))** — treating the geometric series

**Space:**
- Hierarchy storage: O(|V| + |E|) per level × O(log n) levels
- Total: **O((|V| + |E|) log n)**
- Can be reduced to O(|V| + |E|) by discarding coarsened graphs after uncoarsening

### 4.5 Integration with Your Existing ForceLayout

The integration is minimal — your existing `ForceLayout.Run()` already works as a black-box:

```csharp
public class MultiLevelLayout
{
    private readonly GraphModel _original;
    
    public void Run()
    {
        // Phase 1: Build hierarchy
        var hierarchy = BuildHierarchy(_original, minSize: 50);
        
        if (hierarchy.Count == 0)
        {
            // Graph is already small enough — just run FR directly
            new ForceLayout(_original).Run();
            return;
        }
        
        // Phase 2: Layout coarsest graph
        var coarsest = hierarchy[^1].Graph;
        var coarseLayout = new ForceLayout(coarsest)
        {
            Iterations = 500,
        };
        coarseLayout.Randomize();
        coarseLayout.Run();
        
        // Phase 3: Uncoarsen
        for (int i = hierarchy.Count - 1; i >= 0; i--)
        {
            var level = hierarchy[i];
            var fineGraph = (i == 0) ? _original : hierarchy[i - 1].Graph;
            
            // Place fine nodes at their parent's position + jitter
            PlaceFromCoarse(fineGraph, level);
            
            // Refine
            var refineLayout = new ForceLayout(fineGraph)
            {
                Iterations = 50,
            };
            // Don't call Randomize() — we want to keep the inherited positions!
            refineLayout.Run();
        }
    }
    
    private void PlaceFromCoarse(GraphModel fine, CoarseLevel level)
    {
        var rng = new Random(42);
        var coarse = level.Graph;
        
        for (int i = 0; i < fine.Nodes.Count; i++)
        {
            int ci = level.FineToCoarse[i];
            fine.Nodes[i].X = coarse.Nodes[ci].X + (rng.NextDouble() - 0.5) * 5;
            fine.Nodes[i].Y = coarse.Nodes[ci].Y + (rng.NextDouble() - 0.5) * 5;
        }
    }
}
```

**Key integration detail:** Your `ForceLayout.Run()` already checks `if (allZero) Randomize()` — since nodes will have non-zero positions from the coarse placement, it will correctly skip randomization and use the inherited positions. No changes needed to the existing `ForceLayout` class.

---

## 5. Barnes-Hut / Quadtree Optimization

### What It Is

Barnes-Hut is an **approximation for the O(n²) repulsive force calculation**. Instead of computing repulsion between every pair of nodes, it groups distant nodes together and treats them as a single mass.

### How It Works

```
1. Build a QUADTREE over all node positions:
   - Root cell covers the entire layout area
   - Each cell is recursively split into 4 quadrants
   - Each leaf contains at most 1 node
   - Each internal cell stores:
     - Total mass (= sum of node weights, or count of nodes)
     - Center of mass (weighted average of positions)

2. For each node u, TRAVERSE the quadtree to compute repulsive force:
   
   function ComputeRepulsion(u, cell):
       if cell is empty: return (0, 0)
       
       d = distance(u, cell.centerOfMass)
       s = cell.width
       
       if cell is leaf OR (s / d) < θ:    // θ ≈ 1.0 to 1.5
           // Cell is "far enough" — treat as single body
           // Force = k² * cell.mass / d, directed away from center of mass
           return RepulsiveForce(u.position, cell.centerOfMass, cell.mass)
       else:
           // Cell is too close — recurse into children
           return sum of ComputeRepulsion(u, child) for each child
```

**The parameter θ** (theta) controls accuracy vs. speed:
- θ = 0: exact (visits all leaves) — O(n²)
- θ = 0.5: high accuracy — O(n log n), ~2–4× faster than exact for n=10K
- θ = 1.0: typical for graph drawing — O(n log n), ~10–50× faster
- θ = 1.5+: lower accuracy, faster — may produce artifacts

### Data Structure: Quadtree

```csharp
class QuadTreeNode
{
    double CenterX, CenterY;  // Center of mass
    double TotalMass;          // Sum of masses in this cell
    double X, Y, Size;         // Cell bounds (corner + width)
    int NodeIndex;             // -1 if internal, node index if leaf
    QuadTreeNode NW, NE, SW, SE;  // Children (null if leaf)
}
```

**Building the quadtree:** O(n log n) average, O(n²) worst case (all nodes at same position — prevented by jitter).

**Querying for one node:** O(log n) average (with θ > 0).

**Total per iteration:** O(n log n) for build + O(n log n) for all queries = **O(n log n)**.

### How Barnes-Hut Complements Multi-Level Coarsening

They solve **different bottlenecks** and are ideally used together:

| Problem | Multi-Level Coarsening | Barnes-Hut |
|---------|----------------------|------------|
| Local minima | ✅ Solves via coarse-to-fine | ❌ No help |
| O(n²) repulsion | ✅ Reduces n at each level | ✅ Reduces to O(n log n) |
| Many iterations needed | ✅ Fewer iters (warm start) | ❌ Same iteration count |
| Global structure | ✅ Captured at coarse levels | ❌ No help |

**The combination (Multi-Level + Barnes-Hut) is what sfdp uses** and is considered the state-of-the-art for large force-directed layouts:

- Multi-level provides the coarse-to-fine framework
- Barnes-Hut accelerates each FR iteration at every level
- Together: handles 100K+ node graphs in seconds

### Replacing Your Grid Approximation

Your current `RepulsiveApprox` uses a spatial grid with a cutoff distance. Barnes-Hut is superior because:

1. **No cutoff artifacts** — BH accounts for all nodes, just approximately. Your grid ignores nodes beyond 3k distance, which can cause disconnected components to collapse
2. **Adaptive accuracy** — nearby nodes get exact treatment, distant nodes get approximated proportionally to distance
3. **Better for uneven distributions** — your grid cells may be very full in dense areas and empty elsewhere; the quadtree adapts

**Implementation note for your code:** Replace `RepulsiveApprox()` with a quadtree implementation. The interface is the same — it fills the `dx[]` and `dy[]` arrays.

---

## 6. Putting It All Together: Implementation Roadmap for LumenViz

### Phase 1: Barnes-Hut Quadtree (standalone improvement)

Replace `RepulsiveApprox` with a proper quadtree. This alone will improve layout quality for large graphs by removing the distance cutoff.

**Estimated effort:** ~150 lines of C#.

### Phase 2: Multi-Level Coarsening with HEM

Implement the `CoarseLevel`, `CoarseningHierarchy`, and `HeavyEdgeMatch` as described above.

**Estimated effort:** ~200 lines of C#.

### Phase 3: Multi-Level Layout

Wire it together: `BuildHierarchy` → layout coarsest → uncoarsen with refinement.

**Estimated effort:** ~80 lines of C#.

### Phase 4: Tuning

Key parameters to tune:
- **minSize**: when to stop coarsening (50–200, depending on desired speed vs. quality)
- **Refinement iterations per level**: 30–100
- **θ for Barnes-Hut**: 0.8–1.2
- **Initial temperature for refinement**: much lower than for cold-start (the positions are already close to final)
- **Jitter radius**: proportional to ideal edge length at that level

### Expected Speedup on Your Test Graphs

| Graph | Nodes | Current FR | Multi-Level + BH |
|-------|-------|-----------|-------------------|
| karate | 34 | ~instant | No benefit (already tiny) |
| dolphins | 62 | ~instant | No benefit |
| lesmis | 77 | ~instant | No benefit |
| football | 115 | ~instant | Marginal |
| polbooks | 105 | ~instant | Marginal |
| power | 4941 | ~seconds (O(n²) with grid) | ~10-50× faster, better quality |
| 50K graph | 50000 | Minutes to never | Seconds |

**Power grid graph (4941 nodes)** is your current best stress test. Multi-level coarsening will produce dramatically better layouts for it because power grids have long-range structure that flat FR misses — the nodes at opposite ends of the grid never "see" each other through local forces.

---

## References

1. Karypis, G. & Kumar, V. (1998). A Fast and High Quality Multilevel Scheme for Partitioning Irregular Graphs. *SIAM J. Scientific Computing*, 20(1). — *METIS, HEM*
2. Walshaw, C. (2003). A Multilevel Algorithm for Force-Directed Graph Drawing. *J. Graph Algorithms and Applications*, 7(3). — *MIS-based coarsening for layout*
3. Harel, D. & Koren, Y. (2002). A Fast Multi-Scale Method for Drawing Large Graphs. *J. Graph Algorithms and Applications*, 6(3). — *Solar/merger*
4. Gajer, P., Goodrich, M.T. & Kobourov, S.G. (2000). A Multi-Dimensional Approach to Force-Directed Layouts of Large Graphs. *CGF*, 23(3). — *GRIP, MIS filtration*
5. Hu, Y. (2005). Efficient, High-Quality Force-Directed Graph Drawing. *Mathematica Journal*, 10(1). — *sfdp, the gold standard: multi-level + Barnes-Hut + spring-electrical model*
6. Koren, Y., Carmel, L. & Harel, D. (2002). ACE: A Fast Multiscale Eigenvectors Computation for Drawing Huge Graphs. *IEEE InfoVis*. — *AMG spectral approach*
7. Barnes, J. & Hut, P. (1986). A Hierarchical O(N log N) Force-Calculation Algorithm. *Nature*, 324. — *Barnes-Hut*
8. Hachul, S. & Jünger, M. (2004). Drawing Large Graphs with a Potential-Field-Based Multilevel Algorithm. *GD 2004*. — *FM³*
9. Martin, S. et al. (2011). OpenOrd: An Open-Source Toolbox for Large Graph Layout. *SPIE Visualization and Data Analysis*. — *Very large graph layout*
