# Rendering Performance Experiments

> Date: 2026-02-23  
> Graph: `power.mtx` (4,941 nodes, 6,594 edges, 493 communities, 11 coarsening levels)  
> Test level: 0 (finest — all nodes and edges visible)  
> Window: 1280×800  
> Platform: .NET 9.0, GDI+ (System.Drawing), Windows  
> Commit before: `728dd97` (LOD optimizations)  
> Commit after: `6c98595` (instrumentation + experiment controls)

## Background

The lumen-viz graph renderer was recently optimized with several performance
improvements (commit `728dd97`):

- Pre-computed screen coordinates into reusable `float[]` buffers
- LOD rendering: rectangles instead of ellipses when `n > 2000 && zoom < 1.5`
- Anti-aliasing disabled for the graph layer in low-detail mode
- Off-screen indicator cap at 2000 nodes
- Inlined bounds checks for culling

These optimizations were based on **hypotheses about where time was spent** —
but we had no measurements. This document records the instrumentation work,
controlled experiments, and results that followed.

## Hypotheses (Pre-Measurement)

Before adding instrumentation, we predicted:

| # | Hypothesis | Confidence |
|---|-----------|------------|
| H1 | **Edge drawing is the #1 cost** — 6,594 individual `DrawLine` calls | High |
| H2 | Ellipses + outlines are much more expensive than rectangles | High |
| H3 | Anti-aliasing adds significant overhead | Medium |
| H4 | Minimap re-iterating all nodes is costly | Medium |
| H5 | Pre-computed screen coordinates save significant time | Medium |
| H6 | Parent highlight with per-node grouping is moderately expensive | Low |

## Instrumentation Design

### Per-Phase Timing

Added `System.Diagnostics.Stopwatch`-based timing around every phase of
`OnPaint`. Two stopwatches:

- `_frameSw` — measures total frame time (started at top of OnPaint)
- `_phaseSw` — restarted before each phase, read after

Phases measured:
1. `precompute` — screen coordinate transformation
2. `parent_highlight` — community group ellipses
3. `edges` — line drawing with culling
4. `selection_glow` — glow around selected nodes
5. `nodes` — LOD-aware node rendering
6. `off_screen` — viewport edge indicators
7. `labels` — text labels
8. `minimap` — overview overlay
9. `levels_panel` — coarsening level selector
10. `hud` — status bar text

### Exponential Moving Average (EMA)

Each phase tracks both last-frame and EMA-smoothed values:

```csharp
private void UpdateEma(ref double avg, double sample)
{
    if (_perfFrameCount <= 1) avg = sample;
    else avg = avg * (1 - PerfAlpha) + sample * PerfAlpha;
}
```

Alpha = 0.1 gives a ~10-frame smoothing window. This filters out GC pauses
and OS scheduling jitter while still responding to real changes.

### Render Toggle Properties

Added boolean properties to enable/disable individual features without
code changes:

| Property | Default | What it controls |
|----------|---------|-----------------|
| `ShowEdges` | true | Edge drawing pass |
| `ShowNodes` | true | Node drawing pass |
| `ShowOutlines` | true | Ellipse outlines on nodes |
| `ShowOffScreenIndicators` | true | Edge indicators |
| `ShowSelectionGlow` | true | Selected node glow |
| `LodMode` | "auto" | `auto` / `low` (force rects) / `high` (force ellipses) |

Existing toggles: `AntiAlias`, `ShowLabels`, `ShowMinimap`, `ShowLevelsPanel`,
`ShowParentHighlight`.

### MCP Integration

Three new MCP tools:
- `get_render_stats` — returns per-phase last/avg timing + current settings
- `reset_render_stats` — clears EMA for fresh measurement
- `set_render_option` — sets any toggle by name

Also added a **command channel** via `set_label_text` (prefixed with `!render`):
- `!render stats` — get stats
- `!render reset` — reset counters
- `!render set OPTION VALUE` — set a toggle
- `!render warmup N` — run N synchronous repaints and return stats

The warmup command is critical for experiments: it calls `Refresh()` (synchronous
paint) N times in a loop, giving stable EMA values without UI event interference.

### HUD Integration

Frame time is shown in the HUD after 3+ frames warm-up:
```
power  —  4941n, 6594e  |  Level 0/10 (finest)  |  46.2ms
```

## Experimental Methodology

Each experiment follows this protocol:

1. **Load graph** — `power.mtx` into a 1280×800 window
2. **Navigate to level 0** — finest level with all 4,941 nodes
3. **Set configuration** — toggle the feature under test
4. **Reset stats** — clear EMA counters
5. **Warm up 30 frames** — `!render warmup 30` runs 30 synchronous repaints
6. **Record results** — read the EMA averages

30 frames with alpha=0.1 gives the EMA about 95% convergence to the true
steady-state value ($(1-0.1)^{30} ≈ 0.04$, so <4% of the initial value remains).

Control between experiments: only **one variable changes at a time** to isolate
each factor's contribution.

## Results

### Baseline (All Features ON, Level 0)

| Phase | Avg (ms) | % of Frame |
|-------|---------|------------|
| parent_highlight | **440** | **90.5%** |
| edges | 27 | 5.5% |
| nodes (auto LOD → rects) | 9 | 1.8% |
| minimap | 7 | 1.4% |
| levels_panel | 2.4 | 0.5% |
| hud | 0.8 | 0.2% |
| precompute | 0.14 | 0.03% |
| selection_glow | 0.002 | ~0% |
| off_screen | 0.001 | ~0% |
| **TOTAL** | **486** | **~2 FPS** |

### Controlled Experiments

#### Exp 1: Parent Highlight OFF

| Phase | Baseline | Without PH | Savings |
|-------|---------|------------|---------|
| parent_highlight | 440 | 0 | -440ms |
| **TOTAL** | **486** | **46** | **10.5× faster** |

#### Exp 2: Parent Highlight OFF + Edges OFF

| Phase | PH off | PH+Edges off | Savings |
|-------|--------|-------------|---------|
| edges | 27 | 0 | -27ms |
| **TOTAL** | **46** | **22** | **-24ms** |

#### Exp 3: LOD Mode Comparison (Parent Highlight OFF)

| LOD Mode | Nodes (ms) | Total (ms) | Notes |
|----------|-----------|------------|-------|
| auto (→ low) | 9 | 46 | Rects, no AA for graph layer |
| **low** (forced) | 8 | 49 | Same as auto at this scale |
| **high** (forced) | **153** | **195** | Ellipses + outlines + AA |

**Ellipses with outlines are 17× more expensive than rectangles.**

#### Exp 4: Outline Cost (LOD High, Parent Highlight OFF)

| Outlines | Nodes (ms) | Total (ms) |
|----------|-----------|------------|
| ON | 153 | 195 |
| OFF | 39 | 72 |

**Outlines (DrawEllipse per node) cost 114ms — 3× the fill cost alone.**

#### Exp 5: Anti-Aliasing Cost (LOD High, Outlines OFF, PH OFF)

| AA | Edges (ms) | Nodes (ms) | Total (ms) |
|----|-----------|-----------|------------|
| ON | 23 | 39 | 72 |
| OFF | 15 | 21 | 45 |

**AA roughly doubles GDI+ draw costs** (35% edge savings, 46% node savings).

#### Exp 6: Minimap Cost (Default config, PH OFF)

| Minimap | Minimap (ms) | Total (ms) |
|---------|-------------|------------|
| ON | 7 | 46 |
| OFF | 0 | 43 |

Minimap costs **~7ms (15%)** of the non-PH frame time. Measurable but not dominant.

#### Exp 7: Absolute Minimum (Nodes only, auto LOD)

| Config | Total (ms) |
|--------|------------|
| Nodes only (no edges, no overlays) | **11** |

This is the rendering floor: pre-compute + ~5K rectangle fills + HUD.

### Cost Breakdown Summary (Visual)

```
Baseline (486ms):
▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ parent_highlight (440ms)
▓▓▓                                                    edges (27ms)
▓                                                       nodes (9ms)
▓                                                       minimap (7ms)
                                                        rest (<4ms)

Without parent highlight (46ms):
▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓                         edges (27ms)
▓▓▓▓▓▓▓▓▓                                              nodes (9ms)
▓▓▓▓▓▓▓                                                minimap (7ms)
▓▓▓                                                     rest (<4ms)
```

## Hypothesis Evaluation

| # | Hypothesis | Predicted | Actual | Verdict |
|---|-----------|-----------|--------|---------|
| H1 | Edge drawing is #1 cost | Dominant | 27ms (5.5%), **#2** | **WRONG** |
| H2 | Ellipses+outlines >> rectangles | "Much more expensive" | 17× | **Correct** (underestimated magnitude) |
| H3 | AA adds significant overhead | Meaningful | ~2× all primitives | **Correct** |
| H4 | Minimap is costly | Significant concern | 7ms (1.5%) | **Wrong** — negligible |
| H5 | Pre-computed coords save time | Worth doing | 0.14ms | **Correct** but irrelevant to totals |
| H6 | Parent highlight is moderately expensive | Low concern | **440ms (90.5%)** | **Catastrophically underestimated** |

### Key Insight

**The ranking was completely wrong.** The feature that "looked cheap" (parent
highlight — translucent dashed ellipses for community groupings) was the
catastrophic bottleneck, while the feature that "looked expensive" (6,594 edge
DrawLine calls) was 16× smaller.

The parent highlight's cost comes from:
- **493 GDI+ ellipse draws** with alpha-blended fills AND dashed outlines
- Each ellipse is **variable-sized** (computed from node positions per frame)
- Dashed stroke patterns on anti-aliased curves are particularly expensive in GDI+
- **5 fresh float[] allocations** per frame (min/max/count arrays for coarse nodes)

This is a textbook case for **"measure, don't guess."** The human lesson applies
equally to AI: system dynamics are too complex for accurate prediction without
measurement.

## Implications for Future Work

### Immediate Optimization Targets

1. **Parent highlight (440ms)** — the #1 priority by a massive margin:
   - Cache the highlight as a bitmap; invalidate only on zoom/pan/level change
   - Or: skip at level 0 entirely (493 groups is too many to be useful)
   - Or: replace dashed ellipses with simpler shapes (rectangles, or just tinted backgrounds)
   - Or: only draw highlights for visible/on-screen coarse communities

2. **Edge drawing (27ms)** — #2 priority:
   - Batch into a single `DrawLines` call (requires alternating visible/invisible segments)
   - Or: use edge bundling to reduce visual clutter AND draw count

3. **LOD mode switching** — already effective, but the threshold (`n > 2000 && zoom < 1.5`)
   could be tuned based on measured frame times

### Infrastructure Preserved

The instrumentation and toggle system is committed and available for future
experiments. Any new rendering feature should:
1. Be wrapped in a toggle property
2. Have `_phaseSw` timing around it
3. Be testable via the `!render` command channel
