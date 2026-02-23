using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Statistics;
using System.Numerics;

namespace LumenGraph;

/// <summary>
/// Headless graph analytics and statistical analysis.
/// All methods are pure functions operating on GraphModel and double[] data.
/// No dependency on visualization.
/// </summary>
public static class GraphAnalytics
{
    // -----------------------------------------------------------------------
    // Descriptive statistics for any double[] distribution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compute comprehensive descriptive statistics for a data array.
    /// </summary>
    public static DescriptiveResult Describe(double[] data)
    {
        if (data.Length == 0)
            return new DescriptiveResult();

        var sorted = (double[])data.Clone();
        Array.Sort(sorted);

        var stats = new MathNet.Numerics.Statistics.DescriptiveStatistics(data);

        return new DescriptiveResult
        {
            Count = data.Length,
            Mean = stats.Mean,
            StdDev = stats.StandardDeviation,
            Variance = stats.Variance,
            Skewness = stats.Skewness,
            Kurtosis = stats.Kurtosis,
            Min = sorted[0],
            Max = sorted[^1],
            Median = SortedArrayStatistics.Median(sorted),
            Q05 = SortedArrayStatistics.Quantile(sorted, 0.05),
            Q25 = SortedArrayStatistics.Quantile(sorted, 0.25),
            Q75 = SortedArrayStatistics.Quantile(sorted, 0.75),
            Q95 = SortedArrayStatistics.Quantile(sorted, 0.95),
        };
    }

    /// <summary>
    /// Compute specific quantiles for a data array.
    /// </summary>
    public static double[] Quantiles(double[] data, double[] probabilities)
    {
        var sorted = (double[])data.Clone();
        Array.Sort(sorted);
        return probabilities.Select(p => SortedArrayStatistics.Quantile(sorted, p)).ToArray();
    }

    // -----------------------------------------------------------------------
    // Histogram
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a histogram with the specified number of bins.
    /// Returns (binCenters, counts).
    /// </summary>
    public static HistogramResult Histogram(double[] data, int bins = 20)
    {
        if (data.Length == 0)
            return new HistogramResult { BinEdges = [], Counts = [], BinCenters = [] };

        double min = data.Min();
        double max = data.Max();
        if (Math.Abs(max - min) < 1e-15)
        {
            // All values identical
            return new HistogramResult
            {
                BinEdges = [min, max],
                BinCenters = [min],
                Counts = [data.Length],
            };
        }

        double width = (max - min) / bins;
        var edges = new double[bins + 1];
        var centers = new double[bins];
        var counts = new int[bins];

        for (int i = 0; i <= bins; i++)
            edges[i] = min + i * width;
        for (int i = 0; i < bins; i++)
            centers[i] = min + (i + 0.5) * width;

        foreach (double v in data)
        {
            int idx = (int)((v - min) / width);
            if (idx >= bins) idx = bins - 1;
            if (idx < 0) idx = 0;
            counts[idx]++;
        }

        return new HistogramResult
        {
            BinEdges = edges,
            BinCenters = centers,
            Counts = counts,
        };
    }

    // -----------------------------------------------------------------------
    // Degree distribution analysis
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compute degree distribution: returns (degree, count) pairs sorted by degree.
    /// </summary>
    public static (int degree, int count)[] DegreeDistribution(GraphModel graph)
    {
        var freq = new Dictionary<int, int>();
        for (int i = 0; i < graph.NodeCount; i++)
        {
            int d = graph.Degree[i];
            freq.TryGetValue(d, out int c);
            freq[d] = c + 1;
        }
        return freq.OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToArray();
    }

    /// <summary>
    /// Test whether a degree distribution follows a power law.
    /// Returns estimated exponent (alpha) and goodness of fit (R² of log-log regression).
    /// </summary>
    public static (double alpha, double rSquared) PowerLawFit(GraphModel graph)
    {
        var dist = DegreeDistribution(graph);
        // Filter out degree 0
        var points = dist.Where(d => d.degree > 0 && d.count > 0).ToArray();
        if (points.Length < 3)
            return (double.NaN, 0.0);

        // Log-log regression: log(count) = c - alpha * log(degree)
        var logX = points.Select(p => Math.Log(p.degree)).ToArray();
        var logY = points.Select(p => Math.Log(p.count)).ToArray();

        // Simple linear regression
        double meanX = logX.Average();
        double meanY = logY.Average();
        double ssXX = 0, ssXY = 0;
        for (int i = 0; i < logX.Length; i++)
        {
            double dx = logX[i] - meanX;
            ssXX += dx * dx;
            ssXY += dx * (logY[i] - meanY);
        }
        double slope = ssXX > 0 ? ssXY / ssXX : 0;
        double intercept = meanY - slope * meanX;

        // R² goodness of fit
        double ssTot = 0, ssRes = 0;
        for (int i = 0; i < logX.Length; i++)
        {
            double predicted = intercept + slope * logX[i];
            ssTot += (logY[i] - meanY) * (logY[i] - meanY);
            ssRes += (logY[i] - predicted) * (logY[i] - predicted);
        }
        double rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 0.0;

        return (-slope, rSquared); // alpha is negative of the slope
    }

    // -----------------------------------------------------------------------
    // Correlation between metrics
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compute Pearson correlation between two metric arrays.
    /// </summary>
    public static double PearsonCorrelation(double[] x, double[] y)
    {
        return Correlation.Pearson(x, y);
    }

    /// <summary>
    /// Compute Spearman rank correlation between two metric arrays.
    /// </summary>
    public static double SpearmanCorrelation(double[] x, double[] y)
    {
        return Correlation.Spearman(x, y);
    }

    // -----------------------------------------------------------------------
    // FFT / spectral analysis
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compute the FFT power spectrum of a metric distribution.
    /// The metric values are sorted and treated as a signal; the FFT reveals
    /// periodic structure in the distribution.
    /// </summary>
    public static FftResult ComputeSpectrum(double[] data, bool sortFirst = true)
    {
        if (data.Length < 4)
            return new FftResult { Frequencies = [], Magnitudes = [] };

        // Pad to next power of 2 for efficient FFT
        int n = 1;
        while (n < data.Length) n <<= 1;

        var signal = new Complex[n];
        var values = sortFirst ? (double[])data.Clone() : data;
        if (sortFirst) Array.Sort(values);

        // Remove mean (DC component) to focus on structure
        double mean = values.Average();
        for (int i = 0; i < values.Length; i++)
            signal[i] = new Complex(values[i] - mean, 0);
        // Zero-pad remainder
        for (int i = values.Length; i < n; i++)
            signal[i] = Complex.Zero;

        Fourier.Forward(signal, FourierOptions.Matlab);

        // Return only positive frequencies (first half)
        int halfN = n / 2;
        var frequencies = new double[halfN];
        var magnitudes = new double[halfN];
        for (int i = 0; i < halfN; i++)
        {
            frequencies[i] = (double)i / n; // Normalized frequency [0, 0.5)
            magnitudes[i] = signal[i].Magnitude / n;
        }

        return new FftResult
        {
            Frequencies = frequencies,
            Magnitudes = magnitudes,
        };
    }

    /// <summary>
    /// Find dominant frequencies in the spectrum (peaks above threshold * max magnitude).
    /// </summary>
    public static (int index, double frequency, double magnitude)[] FindDominantFrequencies(
        FftResult spectrum, double threshold = 0.1, int maxPeaks = 10)
    {
        if (spectrum.Magnitudes.Length == 0)
            return [];

        double maxMag = spectrum.Magnitudes.Max();
        double cutoff = maxMag * threshold;

        var peaks = new List<(int, double, double)>();
        for (int i = 1; i < spectrum.Magnitudes.Length - 1; i++)
        {
            double m = spectrum.Magnitudes[i];
            if (m > cutoff &&
                m >= spectrum.Magnitudes[i - 1] &&
                m >= spectrum.Magnitudes[i + 1])
            {
                peaks.Add((i, spectrum.Frequencies[i], m));
            }
        }

        return peaks.OrderByDescending(p => p.Item3).Take(maxPeaks).ToArray();
    }

    // -----------------------------------------------------------------------
    // Graph-specific analytics
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find nodes that rank in the top-N for multiple metrics simultaneously.
    /// This is the "power broker" detection pattern.
    /// </summary>
    public static int[] FindCrossRankNodes(GraphModel graph, string[] metrics, int topN = 20)
    {
        var provider = new NodeColorProvider();
        provider.SetGraph(graph);

        // For each metric, get the top-N node indices
        var topSets = new List<HashSet<int>>();
        foreach (var metric in metrics)
        {
            double[] values = provider.ComputeNodeMetric(metric);
            var topIndices = Enumerable.Range(0, graph.NodeCount)
                .OrderByDescending(i => values[i])
                .Take(topN)
                .ToHashSet();
            topSets.Add(topIndices);
        }

        // Intersect all sets
        var result = new HashSet<int>(topSets[0]);
        for (int i = 1; i < topSets.Count; i++)
            result.IntersectWith(topSets[i]);

        return result.OrderBy(i => i).ToArray();
    }

    /// <summary>
    /// Compute a correlation matrix across all metrics for a graph.
    /// Returns (metricNames, correlationMatrix).
    /// </summary>
    public static (string[] names, double[,] matrix) MetricCorrelationMatrix(GraphModel graph)
    {
        var provider = new NodeColorProvider();
        provider.SetGraph(graph);

        var names = NodeColorProvider.MetricNames;
        var metricData = new double[names.Length][];
        for (int i = 0; i < names.Length; i++)
            metricData[i] = provider.ComputeNodeMetric(names[i]);

        var matrix = new double[names.Length, names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            matrix[i, i] = 1.0;
            for (int j = i + 1; j < names.Length; j++)
            {
                double corr = Correlation.Pearson(metricData[i], metricData[j]);
                if (double.IsNaN(corr)) corr = 0;
                matrix[i, j] = corr;
                matrix[j, i] = corr;
            }
        }

        return (names, matrix);
    }

    /// <summary>
    /// Get neighbors of a node with their labels and communities.
    /// </summary>
    public static NodeInfo[] GetNeighbors(GraphModel graph, int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= graph.NodeCount)
            return [];

        var neighbors = graph.Neighbors(nodeIndex);
        var result = new NodeInfo[neighbors.Length];
        for (int i = 0; i < neighbors.Length; i++)
        {
            int n = neighbors[i];
            result[i] = new NodeInfo
            {
                Index = n,
                Label = graph.Labels[n],
                Community = graph.Community[n],
                Degree = graph.Degree[n],
            };
        }
        return result;
    }

    /// <summary>
    /// Get detailed info for a specific node including all metrics.
    /// </summary>
    public static Dictionary<string, object> NodeProfile(GraphModel graph, int nodeIndex)
    {
        var provider = new NodeColorProvider();
        provider.SetGraph(graph);

        var profile = new Dictionary<string, object>
        {
            ["index"] = nodeIndex,
            ["label"] = graph.Labels[nodeIndex],
            ["community"] = graph.Community[nodeIndex],
            ["degree"] = graph.Degree[nodeIndex],
        };

        foreach (var metric in NodeColorProvider.MetricNames)
        {
            double[] values = provider.ComputeNodeMetric(metric);
            profile[metric] = Math.Round(values[nodeIndex], 6);
        }

        return profile;
    }

    // -----------------------------------------------------------------------
    // Result types
    // -----------------------------------------------------------------------

    public class DescriptiveResult
    {
        public int Count { get; set; }
        public double Mean { get; set; }
        public double StdDev { get; set; }
        public double Variance { get; set; }
        public double Skewness { get; set; }
        public double Kurtosis { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Median { get; set; }
        public double Q05 { get; set; }
        public double Q25 { get; set; }
        public double Q75 { get; set; }
        public double Q95 { get; set; }
    }

    public class HistogramResult
    {
        public double[] BinEdges { get; set; } = [];
        public double[] BinCenters { get; set; } = [];
        public int[] Counts { get; set; } = [];
    }

    public class FftResult
    {
        public double[] Frequencies { get; set; } = [];
        public double[] Magnitudes { get; set; } = [];
    }

    public class NodeInfo
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
        public int Community { get; set; }
        public int Degree { get; set; }
    }
}
