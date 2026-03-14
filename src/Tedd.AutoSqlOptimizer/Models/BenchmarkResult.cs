namespace Tedd.AutoSqlOptimizer.Models;

public class BenchmarkResult
{
    public string Label { get; set; } = "";
    public List<SqlTimingResult> Timings { get; set; } = [];

    public int MinCpu => Timings.Count > 0 ? Timings.Min(t => t.CpuTimeMs) : 0;
    public int MaxCpu => Timings.Count > 0 ? Timings.Max(t => t.CpuTimeMs) : 0;
    public double AvgCpu => Timings.Count > 0 ? Timings.Average(t => t.CpuTimeMs) : 0;
    public int MedianCpu => GetMedian(Timings.Select(t => t.CpuTimeMs).ToList());

    public int MinElapsed => Timings.Count > 0 ? Timings.Min(t => t.ElapsedTimeMs) : 0;
    public int MaxElapsed => Timings.Count > 0 ? Timings.Max(t => t.ElapsedTimeMs) : 0;
    public double AvgElapsed => Timings.Count > 0 ? Timings.Average(t => t.ElapsedTimeMs) : 0;
    public int MedianElapsed => GetMedian(Timings.Select(t => t.ElapsedTimeMs).ToList());

    public double GetCpuValue(string metric) => metric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? AvgCpu : MinCpu;
    public double GetElapsedValue(string metric) => metric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? AvgElapsed : MinElapsed;

    private static int GetMedian(List<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
    }
}
