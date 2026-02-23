using System.Globalization;

namespace LumenViz;

/// <summary>
/// Reads Matrix Market (.mtx) exchange format files produced by
/// the SuiteSparse Matrix Collection and similar sources.
///
/// Supports:
///   coordinate pattern symmetric   (unweighted graphs)
///   coordinate integer symmetric   (integer-weighted)
///   coordinate real    symmetric   (real-weighted)
///   coordinate real    general     (directed or asymmetric)
/// </summary>
public static class MatrixMarketReader
{
    public static GraphModel ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return Read(reader, Path.GetFileNameWithoutExtension(path));
    }

    public static GraphModel Read(TextReader reader, string title = "")
    {
        var graph = new GraphModel { Title = title };

        // ── Parse banner line ──────────────────────────────────────────
        string? banner = reader.ReadLine();
        if (banner == null || !banner.StartsWith("%%MatrixMarket"))
            throw new FormatException("Not a Matrix Market file");

        var parts = banner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // parts: %%MatrixMarket  matrix  coordinate  {pattern|integer|real}  {symmetric|general}
        if (parts.Length < 5)
            throw new FormatException($"Incomplete Matrix Market banner: {banner}");

        string qualifierField = parts[3].ToLowerInvariant();  // pattern, integer, real
        string symmetry = parts[4].ToLowerInvariant();         // symmetric, general

        bool isPattern = qualifierField == "pattern";
        bool isSymmetric = symmetry == "symmetric";

        // ── Skip comment lines ─────────────────────────────────────────
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith('%'))
                break;
        }

        if (line == null)
            throw new FormatException("Unexpected end of file before size line");

        // ── Parse size line: rows cols nnz ──────────────────────────────
        var sizeParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int rows = int.Parse(sizeParts[0]);
        int cols = int.Parse(sizeParts[1]);
        int nnz = int.Parse(sizeParts[2]);

        // We treat the matrix as a graph.  rows == cols for square matrices.
        int nodeCount = Math.Max(rows, cols);
        for (int i = 0; i < nodeCount; i++)
            graph.AddNode();

        // ── Parse entries ───────────────────────────────────────────────
        // Track which edges we've added to avoid duplicates when matrix
        // is symmetric and also has both (i,j) and (j,i) listed.
        var edgeSet = new HashSet<(int, int)>();

        for (int k = 0; k < nnz; k++)
        {
            line = reader.ReadLine();
            if (line == null) break;

            var ep = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int i = int.Parse(ep[0]) - 1;  // MTX is 1-indexed
            int j = int.Parse(ep[1]) - 1;

            double weight = 1.0;
            if (!isPattern && ep.Length > 2)
                weight = double.Parse(ep[2], CultureInfo.InvariantCulture);

            // Skip self-loops for graph visualization
            if (i == j) continue;

            // Normalize edge direction so we don't duplicate
            int lo = Math.Min(i, j);
            int hi = Math.Max(i, j);

            if (edgeSet.Add((lo, hi)))
            {
                graph.AddEdge(lo, hi, weight);
            }
        }

        graph.BuildAdjacency();
        return graph;
    }
}
