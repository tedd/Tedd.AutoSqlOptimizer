using Microsoft.Data.SqlClient;

using System.Text;
using System.Text.RegularExpressions;

using Tedd.AutoSqlOptimizer.Models;

namespace Tedd.AutoSqlOptimizer.Services;

public class BenchmarkRunner
{
    private readonly BenchmarkConfig _config;
    private readonly SqlExecutor _sqlExecutor;
    private readonly AiOptimizer _aiOptimizer;
    private readonly ReportGenerator _reportGenerator;
    private readonly Action<string> _log;

    public BenchmarkRunner(BenchmarkConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _sqlExecutor = new SqlExecutor(log);
        _aiOptimizer = new AiOptimizer(config, _sqlExecutor, log);
        _reportGenerator = new ReportGenerator(log);
    }

    public async Task RunAsync(string? specificFolder = null)
    {
        var optimizationsPath = Path.GetFullPath(_config.OptimizationsPath);
        if (!Directory.Exists(optimizationsPath))
        {
            _log($"ERROR: Optimizations folder not found: {optimizationsPath}");
            return;
        }

        var folders = Directory.GetDirectories(optimizationsPath)
            .OrderBy(f => f)
            .ToList();

        if (specificFolder != null)
        {
            folders = folders
                .Where(f => Path.GetFileName(f).Contains(specificFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (_config.IncludePatterns.Count > 0)
        {
            folders = folders
                .Where(f => _config.IncludePatterns.Any(p => Regex.IsMatch(Path.GetFileName(f), p, RegexOptions.IgnoreCase)))
                .ToList();
            _log($"Include patterns applied ({string.Join(", ", _config.IncludePatterns)}): {folders.Count} folder(s) remaining.");
        }

        if (_config.ExcludePatterns.Count > 0)
        {
            folders = folders
                .Where(f => !_config.ExcludePatterns.Any(p => Regex.IsMatch(Path.GetFileName(f), p, RegexOptions.IgnoreCase)))
                .ToList();
            _log($"Exclude patterns applied ({string.Join(", ", _config.ExcludePatterns)}): {folders.Count} folder(s) remaining.");
        }

        if (folders.Count == 0)
        {
            _log("No optimization folders found.");
            return;
        }

        _log($"Found {folders.Count} optimization folder(s) to process.");

        // Create run output folder
        var runTimestamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        var runFolder = Path.GetFullPath(Path.Combine(_config.OutputPath, runTimestamp));
        Directory.CreateDirectory(runFolder);
        _log($"Run output folder: {runFolder}");

        // Set up file logging
        var logFile = Path.Combine(runFolder, "run.log");
        var fileLog = new StreamWriter(logFile, append: true) { AutoFlush = true };
        var originalLog = _log;
        void combinedLog(string msg)
        {
            var timestampedMsg = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            originalLog(timestampedMsg);
            fileLog.WriteLine(timestampedMsg);
        }

        // Replace _log references in child services by creating new instances
        var sqlExecutor = new SqlExecutor(combinedLog);
        var aiOptimizer = new AiOptimizer(_config, sqlExecutor, combinedLog);
        var reportGenerator = new ReportGenerator(combinedLog);

        var summaries = folders.Select(f => new OptimizationSummary { FolderName = Path.GetFileName(f) }).ToList();

        try
        {
            var initSqlPathsToTry = new[]
            {
                Path.Combine(optimizationsPath,  "init.sql")
            };

            var initSqlPath = initSqlPathsToTry.FirstOrDefault(File.Exists);

            if (initSqlPath != null)
            {
                combinedLog($"\n--- Executing init.sql ({initSqlPath}) ---");
                var initSql = await File.ReadAllTextAsync(initSqlPath);

                var builder = new SqlConnectionStringBuilder(_config.ConnectionString);
                if (initSql.Contains("RESTORE DATABASE", StringComparison.OrdinalIgnoreCase))
                {
                    builder.InitialCatalog = "master";
                }

                using var initConn = new SqlConnection(builder.ConnectionString);
                await initConn.OpenAsync();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var batches = System.Text.RegularExpressions.Regex.Split(initSql, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
                foreach (var batch in batches)
                {
                    var trimmed = batch.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    combinedLog($"[DEBUG SQL] Executing init batch:\n{trimmed}");
                    var batchSw = System.Diagnostics.Stopwatch.StartNew();
                    using var cmd = new SqlCommand(trimmed, initConn);
                    cmd.CommandTimeout = 1200;
                    await cmd.ExecuteNonQueryAsync();
                    batchSw.Stop();
                    combinedLog($"[DEBUG SQL] Batch executed in {batchSw.ElapsedMilliseconds} ms");
                }
                sw.Stop();
                combinedLog($"--- init.sql execution completed in {sw.ElapsedMilliseconds} ms ---\n");
            }

            using var conn = new SqlConnection(_config.ConnectionString);
            conn.Open();
            combinedLog($"Connected to SQL Server: {conn.ServerVersion}");

            foreach (var folder in folders)
            {
                var summary = summaries.FirstOrDefault(s => s.FolderName == Path.GetFileName(folder));
                if (summary != null) summary.Status = "Running";
                reportGenerator.GenerateSummaryReport(runFolder, summaries, _config.TimingMetric);

                await ProcessOptimizationFolder(conn, folder, runFolder, sqlExecutor, aiOptimizer, reportGenerator, combinedLog, summary, summaries);

                if (summary != null && summary.Status == "Running") summary.Status = "Done";
                reportGenerator.GenerateSummaryReport(runFolder, summaries, _config.TimingMetric);
            }
        }
        catch (Exception ex)
        {
            combinedLog($"FATAL ERROR: {ex}");
        }
        finally
        {
            fileLog.Dispose();
        }

        _log($"\nRun complete. Results in: {runFolder}");
    }

    private async Task ProcessOptimizationFolder(
        SqlConnection conn,
        string folderPath,
        string runFolder,
        SqlExecutor sqlExecutor,
        AiOptimizer aiOptimizer,
        ReportGenerator reportGenerator,
        Action<string> log,
        OptimizationSummary? summary,
        List<OptimizationSummary> allSummaries)
    {
        var optimization = OptimizationFolder.Load(folderPath);
        log($"{new string('=', 60)}");
        log($"Processing: {optimization.Name}");
        log($"{new string('=', 60)}");
        log($"  AI Mode: {optimization.IsAiMode}");
        log($"  Before SQL ({optimization.BeforeSql.Length} chars): {optimization.BeforeSql[..Math.Min(200, optimization.BeforeSql.Length)]}...");

        var outputFolder = Path.Combine(runFolder, optimization.Name);
        Directory.CreateDirectory(outputFolder);

        // Save the SQL files to output for reference
        File.WriteAllText(Path.Combine(outputFolder, "1_before.sql"), optimization.BeforeSql);
        File.WriteAllText(Path.Combine(outputFolder, "3_after.sql"), optimization.AfterSql);
        if (!optimization.IsAiMode)
        {
            File.WriteAllText(Path.Combine(outputFolder, "2_optimize.sql"), optimization.OptimizeSql);
            File.WriteAllText(Path.Combine(outputFolder, "4_revert.sql"), optimization.RevertSql);
        }

        BenchmarkResult beforeResult = new BenchmarkResult { Label = "Before" };
        BenchmarkResult? afterResult = null;
        List<AiOptimizer.AiOptimizationResult>? aiResults = null;
        var optimizationName = optimization.Name;

        string? benchmarkError = null;
        try
        {
            // Phase 1: Warm-up
            log($"--- Warm-up ({_config.WarmUpIterations} iterations) ---");
            sqlExecutor.UpdateStatistics(conn);
            for (int i = 1; i <= _config.WarmUpIterations; i++)
            {
                sqlExecutor.ClearCache(conn);
                var warmUpTiming = sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                log($"  Warm-up {i}: {warmUpTiming}");
            }

            // Phase 2: Before measurement
            log($"--- Before Measurement ({_config.BenchmarkIterations} iterations) ---");
            beforeResult = new BenchmarkResult { Label = "Before" };
            for (int i = 1; i <= _config.BenchmarkIterations; i++)
            {
                sqlExecutor.ClearCache(conn);
                var timing = sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                beforeResult.Timings.Add(timing);
                log($"  Run {i}: {timing}");
            }
            var valCpu = beforeResult.GetCpuValue(_config.TimingMetric);
            var valElapsed = beforeResult.GetElapsedValue(_config.TimingMetric);
            var metricLabel = _config.TimingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";

            log($"  Before results ({_config.TimingMetric}): {metricLabel}CPU={valCpu:F0}ms, {metricLabel}Elapsed={valElapsed:F0}ms, " +
                $"MedianCPU={beforeResult.MedianCpu}ms, MedianElapsed={beforeResult.MedianElapsed}ms");

            if (summary != null)
            {
                summary.BeforeCpu = beforeResult.GetCpuValue(_config.TimingMetric);
                summary.BeforeElapsed = beforeResult.GetElapsedValue(_config.TimingMetric);
                reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric);
            }

            if (optimization.IsAiMode)
            {
                // AI mode
                log("--- AI Optimization Mode ---");
                aiResults = await aiOptimizer.RunAiOptimizationsAsync(conn, optimization, beforeResult, outputFolder, (attempt) =>
                {
                    if (summary != null)
                    {
                        summary.AiAttempts.Add(attempt);

                        // Find best successful AI attempt so far
                        var best = summary.AiAttempts
                            .Where(a => a.AfterResult != null && a.OptimizeSucceeded)
                            .OrderBy(a => a.AfterResult!.GetElapsedValue(_config.TimingMetric))
                            .FirstOrDefault();

                        if (best != null)
                        {
                            summary.BestAfterCpu = best.AfterResult!.GetCpuValue(_config.TimingMetric);
                            summary.BestAfterElapsed = best.AfterResult!.GetElapsedValue(_config.TimingMetric);
                            summary.BestStrategy = best.Name;
                        }

                        reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric);
                    }
                });
            }
            else
            {
                // Manual optimization
                log("--- Applying Optimization ---");
                sqlExecutor.ExecuteNonQuery(conn, optimization.OptimizeSql);
                log("  Optimization applied successfully.");

                // Update statistics after applying optimization schema/index changes
                sqlExecutor.UpdateStatistics(conn);

                // Phase 3: After measurement
                log("--- After Measurement ({_config.BenchmarkIterations} iterations) ---");
                afterResult = new BenchmarkResult { Label = "After" };
                for (int i = 1; i <= _config.BenchmarkIterations; i++)
                {
                    sqlExecutor.ClearCache(conn);
                    var timing = sqlExecutor.ExecuteWithTiming(conn, optimization.AfterSql);
                    afterResult.Timings.Add(timing);
                    log($"  Run {i}: {timing}");
                }
                var valCpuA = afterResult.GetCpuValue(_config.TimingMetric);
                var valElapsedA = afterResult.GetElapsedValue(_config.TimingMetric);
                var metricLabelA = _config.TimingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";

                log($"  After results ({_config.TimingMetric}): {metricLabelA}CPU={valCpuA:F0}ms, {metricLabelA}Elapsed={valElapsedA:F0}ms, " +
                    $"MedianCPU={afterResult.MedianCpu}ms, MedianElapsed={afterResult.MedianElapsed}ms");

                if (summary != null)
                {
                    summary.BestAfterCpu = afterResult.GetCpuValue(_config.TimingMetric);
                    summary.BestAfterElapsed = afterResult.GetElapsedValue(_config.TimingMetric);
                    summary.BestStrategy = "Manual";
                    reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric);
                }

                // Phase 4: Revert
                log("--- Reverting Optimization ---");
                sqlExecutor.ExecuteNonQuery(conn, optimization.RevertSql);
                log("  Revert applied successfully.");

                // Update statistics after reverting changes
                sqlExecutor.UpdateStatistics(conn);

                // Verify revert
                log("  Verifying revert...");
                sqlExecutor.ClearCache(conn);
                var verifyTiming = sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                log($"  Verification timing: {verifyTiming}");
            }
        }
        catch (Exception ex)
        {
            benchmarkError = ex.ToString();
            log($"  ERROR during benchmark phases: {ex.Message}");
            if (summary != null) summary.Status = "Failed";
        }

        // Generate reports
        log("--- Generating Reports ---");
        try
        {
            var finalAiResults = aiResults ?? summary?.AiAttempts;
            reportGenerator.GenerateMarkdownReport(outputFolder, optimizationName, beforeResult, afterResult, _config.TimingMetric, finalAiResults, benchmarkError);
            reportGenerator.GenerateHtmlReport(outputFolder, optimizationName, beforeResult, afterResult, _config.TimingMetric, finalAiResults, benchmarkError);
        }
        catch (Exception ex)
        {
            log($"  ERROR generating reports: {ex.Message}");
        }

        log($"\nDone processing {optimization.Name}.");
    }
}
