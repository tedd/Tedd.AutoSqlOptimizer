using Tedd.AutoSqlOptimizer.Services;

namespace Tedd.AutoSqlOptimizer.Models;

public class OptimizationSummary
{
    public string FolderName { get; set; } = "";
    public string Status { get; set; } = "Pending"; // Pending, Running, Done, Failed
    public double? BeforeCpu { get; set; }
    public double? BeforeElapsed { get; set; }
    public double? BestAfterCpu { get; set; }
    public double? BestAfterElapsed { get; set; }
    public string BestStrategy { get; set; } = "None";
    public List<AiOptimizer.AiOptimizationResult> AiAttempts { get; set; } = new();
}
