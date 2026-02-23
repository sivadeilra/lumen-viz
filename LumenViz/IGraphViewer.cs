using LumenGraph;

namespace LumenViz;

/// <summary>
/// Common interface for all graph viewer controls (force-directed, matrix, etc.).
/// Each implementation is also a <see cref="System.Windows.Forms.Control"/> that
/// can be docked inside a <see cref="VizWindow"/>.
/// </summary>
public interface IGraphViewer
{
    // ── Identity ────────────────────────────────────────────────────────

    /// <summary>
    /// Short name identifying this viewer type (e.g. "force", "matrix").
    /// </summary>
    string ViewerType { get; }

    // ── Graph data ──────────────────────────────────────────────────────

    void SetGraph(GraphModel graph);
    void SetGraphWithHierarchy(GraphModel graph, CoarseningHierarchy hierarchy);
    GraphModel? GetGraph();
    CoarseningHierarchy? GetHierarchy();

    // ── Viewport ────────────────────────────────────────────────────────

    void AutoFit();

    // ── Selection ───────────────────────────────────────────────────────

    IReadOnlyCollection<int> Selection { get; }
    void ClearSelection();
    void SetSelection(IEnumerable<int> indices);
    void SelectAll();
    event Action? SelectionChanged;

    // ── Coarsening levels ───────────────────────────────────────────────

    int CurrentLevel { get; }
    int LevelCount { get; }
    bool SetLevel(int level);
    void GoCoarser();
    void GoFiner();
    event Action? LevelChanged;

    // ── Visual settings ─────────────────────────────────────────────────

    float NodeRadius { get; set; }
    float EdgeAlpha { get; set; }
    bool ShowLabels { get; set; }
    bool ShowMinimap { get; set; }
    bool ShowEdges { get; set; }
    bool ShowNodes { get; set; }
    string NodeColorMode { get; set; }
    string EdgeColorMode { get; set; }

    // ── Performance instrumentation ─────────────────────────────────────

    Dictionary<string, object> GetRenderStats();
    void ResetRenderStats();
}
