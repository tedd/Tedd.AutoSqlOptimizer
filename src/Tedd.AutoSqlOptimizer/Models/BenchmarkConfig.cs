namespace Tedd.AutoSqlOptimizer.Models;

public class BenchmarkConfig
{
    public string ConnectionString { get; set; } = "";
    public string DatabaseType { get; set; } = "MSSQL";
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
    /// <summary>
    /// When true, init.sql is executed once before each optimization folder (test type) instead of
    /// only once at the start of the entire run. Has no effect if init.sql does not exist.
    /// </summary>
    public bool RunInitBeforeEachTest { get; set; } = false;
    /// <summary>
    /// When true, init.sql is executed before the next test if the previous test's revert failed
    /// (or threw an exception after the optimization was applied), to restore a clean DB state.
    /// Has no effect if init.sql does not exist.
    /// </summary>
    public bool RunInitBeforeNextTestIfRevertFailed { get; set; } = false;
}

public class OpenAiConfig
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5.4-2026-03-05";
}
