using Tedd.AutoSqlOptimizer.Services;

namespace Tedd.AutoSqlOptimizer.Models;

public class OptimizationSummary
{
    public string FolderName { get; set; } = "";
    public string Status { get; set; } = "Pending"; // Pending, Running, Done, Failed, Cancelled
    public double? BeforeCpu { get; set; }
    public double? BeforeElapsed { get; set; }
    public double? BestAfterCpu { get; set; }
    public double? BestAfterElapsed { get; set; }
    public string BestStrategy { get; set; } = "None";
    public List<AiOptimizer.AiOptimizationResult> AiAttempts { get; set; } = new();

    // Additional tracking fields
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan? Duration => EndTime.HasValue && StartTime.HasValue ? EndTime.Value - StartTime.Value : null;
    public int AiIterationCount { get; set; }
    public bool IsManual { get; set; }
    public string OutputFolderName { get; set; } = "";
}
