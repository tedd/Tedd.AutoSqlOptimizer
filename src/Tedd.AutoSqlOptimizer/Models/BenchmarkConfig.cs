namespace Tedd.AutoSqlOptimizer.Models;

public class BenchmarkConfig
{
    public string ConnectionString { get; set; } = "";
    public OpenAiConfig OpenAI { get; set; } = new();
    public int BenchmarkIterations { get; set; } = 10;
    public int WarmUpIterations { get; set; } = 2;
    public int AiMaxRetries { get; set; } = 3;
    public int AiOptimizationCount { get; set; } = 5;
    public string TimingMetric { get; set; } = "Lowest"; // "Lowest" or "Average"
    public string OptimizationsPath { get; set; } = "Optimize";
    public string OutputPath { get; set; } = "Runs";
    public string? IntegrityCheckSkipPattern { get; set; }
    public List<string> IncludePatterns { get; set; } = [];
    public List<string> ExcludePatterns { get; set; } = [];
}

public class OpenAiConfig
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5.4-2026-03-05";
}
