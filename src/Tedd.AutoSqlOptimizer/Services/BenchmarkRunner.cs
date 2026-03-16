using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;

using Tedd.AutoSqlOptimizer.Models;

namespace Tedd.AutoSqlOptimizer.Services;

public class BenchmarkRunner
{
    private readonly BenchmarkConfig _config;
    private readonly IDatabaseExecutor _sqlExecutor;
    private readonly AiOptimizer _aiOptimizer;
    private readonly ReportGenerator _reportGenerator;
    private readonly Action<string> _log;

    // Shared state for the background status-bar updater
    private volatile int _currentFolderIndex = 0;
    private volatile int _totalFolders = 0;
    private volatile string _currentPhase = "";
    private volatile string _currentFolder = "";
    private volatile int _phaseIterCurrent = 0;
    private volatile int _phaseIterTotal = 0;
    private DateTime _runStartTime;

    // History of completed-optimization durations (for ETA)
    private readonly List<TimeSpan> _completedDurations = [];

    public BenchmarkRunner(BenchmarkConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _sqlExecutor = DatabaseExecutorFactory.Create(config, log);
        _aiOptimizer = new AiOptimizer(config, _sqlExecutor, log);
        _reportGenerator = new ReportGenerator(log);
    }

    public async Task RunAsync(string? specificFolder = null, CancellationToken cancellationToken = default)
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
            folders = folders
                .Where(f => Path.GetFileName(f).Contains(specificFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

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

        _totalFolders = folders.Count;
        _currentFolderIndex = 0;

        // Create run output folder
        _runStartTime = DateTime.Now;
        var runTimestamp = _runStartTime.ToString("yyyy-MM-dd HHmmss");
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

        var sqlExecutor = DatabaseExecutorFactory.Create(_config, combinedLog);
        var aiOptimizer = new AiOptimizer(_config, sqlExecutor, combinedLog);
        var reportGenerator = new ReportGenerator(combinedLog);

        var summaries = folders.Select(f => new OptimizationSummary { FolderName = Path.GetFileName(f) }).ToList();

        // ── Background status-bar updater ─────────────────────────────────────
        using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var statusTask = Task.Run(async () =>
        {
            while (!statusCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(500, statusCts.Token); }
                catch (OperationCanceledException) { break; }
                UpdateStatusBar(summaries);
            }
        }, statusCts.Token);

        // Print initial status board
        PrintStatusBoard(summaries, cancellationToken.IsCancellationRequested);

        bool cancelled = false;
        try
        {
            var initSqlPath = new[] { Path.Combine(optimizationsPath, "init.sql") }
                .FirstOrDefault(File.Exists);

            if (initSqlPath != null && !_config.RunInitBeforeEachTest)
                await sqlExecutor.ExecuteInitSqlAsync(initSqlPath, _config.ConnectionString, combinedLog, cancellationToken);

            using var conn = await sqlExecutor.OpenConnectionAsync(_config.ConnectionString, cancellationToken);
            combinedLog($"Connected: {conn.ServerVersion}");

            var previousRevertFailed = false;
            for (int fi = 0; fi < folders.Count; fi++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folder = folders[fi];
                _currentFolderIndex = fi + 1;
                _currentFolder = Path.GetFileName(folder);
                _currentPhase = "Initializing";
                _phaseIterCurrent = 0;
                _phaseIterTotal = 0;

                if (initSqlPath != null && _config.RunInitBeforeEachTest)
                    await sqlExecutor.ExecuteInitSqlAsync(initSqlPath, _config.ConnectionString, combinedLog, cancellationToken);
                else if (initSqlPath != null && _config.RunInitBeforeNextTestIfRevertFailed && previousRevertFailed)
                {
                    combinedLog("  Previous revert failed — running init.sql to restore clean DB state.");
                    await sqlExecutor.ExecuteInitSqlAsync(initSqlPath, _config.ConnectionString, combinedLog, cancellationToken);
                }

                var summary = summaries.FirstOrDefault(s => s.FolderName == Path.GetFileName(folder));
                if (summary != null)
                {
                    summary.Status = "Running";
                    summary.StartTime = DateTime.Now;
                }

                // Write summary before starting each optimization
                reportGenerator.GenerateSummaryReport(runFolder, summaries, _config.TimingMetric, _runStartTime);
                PrintStatusBoard(summaries, cancellationToken.IsCancellationRequested);

                previousRevertFailed = await ProcessOptimizationFolder(
                    conn, folder, runFolder, sqlExecutor, aiOptimizer, reportGenerator,
                    combinedLog, summary, summaries, cancellationToken);

                if (summary != null)
                {
                    if (summary.Status == "Running") summary.Status = "Done";
                    summary.EndTime = DateTime.Now;
                    if (summary.Duration.HasValue)
                        _completedDurations.Add(summary.Duration.Value);
                }

                // Write summary + print board after each optimization completes
                reportGenerator.GenerateSummaryReport(runFolder, summaries, _config.TimingMetric, _runStartTime);
                PrintStatusBoard(summaries, cancellationToken.IsCancellationRequested);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            combinedLog("  Run cancelled by user.");

            // Mark any "Running" items as Cancelled and generate their reports
            foreach (var s in summaries.Where(s => s.Status == "Running"))
            {
                s.Status = "Cancelled";
                s.EndTime = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            combinedLog($"FATAL ERROR: {ex}");
        }
        finally
        {
            // Stop status bar updater
            statusCts.Cancel();
            try { await statusTask; } catch { /* ignore */ }

            // Always write final summary
            try
            {
                reportGenerator.GenerateSummaryReport(runFolder, summaries, _config.TimingMetric, _runStartTime);
            }
            catch (Exception ex)
            {
                originalLog($"WARNING: Could not write final summary report: {ex.Message}");
            }

            fileLog.Dispose();
        }

        _currentPhase = "";
        _currentFolder = "";
        ConsoleDisplay.ClearStatus();

        PrintStatusBoard(summaries, cancelled);

        _log($"\n{(cancelled ? "⚠  Run cancelled." : "✓  Run complete.")}  Results in: {runFolder}");
    }

    // ── Status bar & board ────────────────────────────────────────────────────

    private void UpdateStatusBar(List<OptimizationSummary> summaries)
    {
        var elapsed = DateTime.Now - _runStartTime;
        var done = summaries.Count(s => s.Status is "Done" or "Failed" or "Cancelled");
        var total = summaries.Count;
        var pct = total > 0 ? (double)done / total * 100 : 0;
        var bar = ConsoleDisplay.ProgressBar(done, total, 16);
        var spinner = ConsoleDisplay.NextSpinner();

        var etaStr = BuildEta(elapsed, done, total);

        var phaseInfo = string.IsNullOrEmpty(_currentPhase)
            ? ""
            : $" │ {_currentFolder}: {_currentPhase}" +
              (_phaseIterTotal > 0 ? $" {_phaseIterCurrent}/{_phaseIterTotal}" : "");

        var line = $" {spinner} [{bar}] {done}/{total} ({pct:F0}%)  ⏱ {ConsoleDisplay.FormatDuration(elapsed)}{etaStr}{phaseInfo}";
        ConsoleDisplay.SetStatus(line, ConsoleColor.DarkCyan);
    }

    private string BuildEta(TimeSpan elapsed, int done, int total)
    {
        if (done <= 0 || _completedDurations.Count == 0) return "";
        var avgDuration = TimeSpan.FromMilliseconds(_completedDurations.Average(d => d.TotalMilliseconds));
        var remaining = total - done;
        var eta = TimeSpan.FromMilliseconds(avgDuration.TotalMilliseconds * remaining);
        return $"  ETA: ~{ConsoleDisplay.FormatDuration(eta)}";
    }

    private void PrintStatusBoard(List<OptimizationSummary> summaries, bool cancelled)
    {
        var elapsed = DateTime.Now - _runStartTime;
        var done = summaries.Count(s => s.Status is "Done" or "Failed" or "Cancelled");
        var total = summaries.Count;
        var pct = total > 0 ? (double)done / total * 100 : 0;
        var bar = ConsoleDisplay.ProgressBar(done, total, 20);
        var etaStr = BuildEta(elapsed, done, total);

        var header = cancelled
            ? $"  ⚠  CANCELLED   {done}/{total} ({pct:F0}%)  ⏱ {ConsoleDisplay.FormatDuration(elapsed)}"
            : $"  Progress: {done}/{total} ({pct:F0}%)  ⏱ {ConsoleDisplay.FormatDuration(elapsed)}{etaStr}";

        // Determine board width
        var maxName = summaries.Count > 0 ? summaries.Max(s => s.FolderName.Length) : 20;
        maxName = Math.Max(maxName, 20);
        var boardWidth = maxName + 42;
        boardWidth = Math.Max(boardWidth, header.Length + 4);

        var border = new string('─', boardWidth - 2);
        var lines = new List<(string text, ConsoleColor color)>
        {
            ($"┌{border}┐", ConsoleColor.DarkCyan),
            ($"│{header.PadRight(boardWidth - 2)}│", ConsoleColor.Cyan),
            ($"│  [{bar}]{"".PadRight(boardWidth - 2 - bar.Length - 4)}│", ConsoleColor.DarkCyan),
            ($"├{border}┤", ConsoleColor.DarkCyan),
        };

        foreach (var s in summaries)
        {
            var (icon, statusColor) = s.Status switch
            {
                "Done"      => ("✓", ConsoleColor.Green),
                "Failed"    => ("✗", ConsoleColor.Red),
                "Cancelled" => ("⚠", ConsoleColor.Yellow),
                "Running"   => ("⚙", ConsoleColor.Cyan),
                _           => ("○", ConsoleColor.DarkGray),
            };

            var duration = s.Duration.HasValue ? ConsoleDisplay.FormatDuration(s.Duration.Value) : "--:--";

            string improvement;
            if (s.BestAfterElapsed.HasValue && s.BeforeElapsed.HasValue && s.BeforeElapsed > 0)
            {
                var imp = (1.0 - s.BestAfterElapsed.Value / s.BeforeElapsed.Value) * 100;
                improvement = $"{imp:+0.0;-0.0}%".PadLeft(7);
            }
            else if (s.Status == "Running")
            {
                improvement = " (active)";
            }
            else
            {
                improvement = "       ";
            }

            var name = s.FolderName.Length > maxName ? s.FolderName[..maxName] : s.FolderName.PadRight(maxName);
            var statusStr = s.Status.PadRight(9);
            var row = $"│  {icon}  {name}  {statusStr}  {duration}  {improvement}  │";
            lines.Add((row, statusColor));
        }

        lines.Add(($"└{border}┘", ConsoleColor.DarkCyan));
        lines.Add(("", ConsoleColor.Gray));

        ConsoleDisplay.PrintBlock(lines);
    }

    // ── per-optimization folder processing ───────────────────────────────────

    /// <returns>True if the revert failed, meaning the DB may be in a dirty state.</returns>
    private async Task<bool> ProcessOptimizationFolder(
        DbConnection conn,
        string folderPath,
        string runFolder,
        IDatabaseExecutor sqlExecutor,
        AiOptimizer aiOptimizer,
        ReportGenerator reportGenerator,
        Action<string> log,
        OptimizationSummary? summary,
        List<OptimizationSummary> allSummaries,
        CancellationToken cancellationToken)
    {
        var optimization = OptimizationFolder.Load(folderPath);

        // Generate missing SQL files from AI_Input.txt if present
        if (!string.IsNullOrWhiteSpace(optimization.AiInput))
        {
            if (string.IsNullOrWhiteSpace(optimization.BeforeSql))
            {
                log($"  AI_Input.txt found and 1_before.sql is missing — asking AI to generate it...");
                var generated = await aiOptimizer.GenerateMissingSqlAsync(optimization.AiInput, "1_before");
                if (generated != null)
                {
                    optimization.BeforeSql = generated;
                    log($"  Generated 1_before.sql ({generated.Length} chars).");
                    File.WriteAllText(Path.Combine(folderPath, "1_before.sql"), generated);
                }
                else
                {
                    log($"  WARNING: Could not generate 1_before.sql from AI_Input.txt.");
                }
            }

            if (string.IsNullOrWhiteSpace(optimization.AfterSql) && !string.IsNullOrWhiteSpace(optimization.BeforeSql))
            {
                log($"  3_after.sql is missing — defaulting to 1_before.sql.");
                optimization.AfterSql = optimization.BeforeSql;
            }
        }
        else if (string.IsNullOrWhiteSpace(optimization.AfterSql) && !string.IsNullOrWhiteSpace(optimization.BeforeSql))
        {
            log($"  3_after.sql is missing — defaulting to 1_before.sql.");
            optimization.AfterSql = optimization.BeforeSql;
        }

        if (string.IsNullOrWhiteSpace(optimization.BeforeSql))
        {
            log($"  ERROR: No 1_before.sql and no AI_Input.txt to generate it from. Skipping folder.");
            return false;
        }

        log($"{new string('=', 60)}");
        log($"Processing: {optimization.Name}");
        log($"{new string('=', 60)}");
        log($"  AI Mode: {optimization.IsAiMode}");
        log($"  AI Input: {(string.IsNullOrWhiteSpace(optimization.AiInput) ? "(none)" : $"{optimization.AiInput.Length} chars")}");
        log($"  Before SQL ({optimization.BeforeSql.Length} chars): {optimization.BeforeSql[..Math.Min(200, optimization.BeforeSql.Length)]}...");

        var outputFolder = Path.Combine(runFolder, optimization.Name);
        Directory.CreateDirectory(outputFolder);

        if (summary != null)
        {
            summary.OutputFolderName = optimization.Name;
            summary.IsManual = !optimization.IsAiMode;
        }

        // Save SQL files to output for reference
        if (!string.IsNullOrWhiteSpace(optimization.AiInput))
            File.WriteAllText(Path.Combine(outputFolder, "AI_Input.txt"), optimization.AiInput);
        File.WriteAllText(Path.Combine(outputFolder, "1_before.sql"), optimization.BeforeSql);
        File.WriteAllText(Path.Combine(outputFolder, "3_after.sql"), optimization.AfterSql);
        if (!optimization.IsAiMode)
        {
            File.WriteAllText(Path.Combine(outputFolder, "2_optimize.sql"), optimization.OptimizeSql);
            File.WriteAllText(Path.Combine(outputFolder, "4_revert.sql"), optimization.RevertSql);
        }

        BenchmarkResult beforeResult = new() { Label = "Before" };
        BenchmarkResult? afterResult = null;
        List<AiOptimizer.AiOptimizationResult>? aiResults = null;
        var optimizationName = optimization.Name;

        string? benchmarkError = null;
        bool optimizeApplied = false;
        bool revertFailed = false;
        try
        {
            // Phase 1: Warm-up
            _currentPhase = "Warm-up";
            _phaseIterTotal = _config.WarmUpIterations;
            log($"--- Warm-up ({_config.WarmUpIterations} iterations) ---");
            sqlExecutor.UpdateStatistics(conn);
            for (int i = 1; i <= _config.WarmUpIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _phaseIterCurrent = i;
                sqlExecutor.ClearCache(conn);
                var warmUpTiming = sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                log($"  Warm-up {i}: {warmUpTiming}");
            }

            // Phase 2: Before measurement
            _currentPhase = "Before";
            _phaseIterTotal = _config.BenchmarkIterations;
            log($"--- Before Measurement ({_config.BenchmarkIterations} iterations) ---");
            beforeResult = new BenchmarkResult { Label = "Before" };
            for (int i = 1; i <= _config.BenchmarkIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _phaseIterCurrent = i;
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
                // Write reports so partial data is available immediately
                reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric, _runStartTime);
                SafeGenerateDetailReports(reportGenerator, outputFolder, optimizationName, beforeResult, null, aiResults, benchmarkError, log);
            }

            if (optimization.IsAiMode)
            {
                _currentPhase = "AI Optimization";
                _phaseIterTotal = _config.AiOptimizationCount;
                _phaseIterCurrent = 0;
                log("--- AI Optimization Mode ---");

                aiResults = await aiOptimizer.RunAiOptimizationsAsync(conn, optimization, beforeResult, outputFolder, (attempt) =>
                {
                    if (summary != null)
                    {
                        _phaseIterCurrent++;
                        summary.AiAttempts.Add(attempt);

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

                        // Write reports after every AI attempt
                        reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric, _runStartTime);
                        SafeGenerateDetailReports(reportGenerator, outputFolder, optimizationName, beforeResult, null, summary.AiAttempts, benchmarkError, log);
                    }
                });

                revertFailed = aiResults?.Any(r => !r.RevertSucceeded) ?? false;
                if (summary != null)
                    summary.AiIterationCount = aiResults?.Count ?? 0;
            }
            else
            {
                // Manual optimization
                _currentPhase = "Applying";
                _phaseIterTotal = 0;
                log("--- Applying Optimization ---");
                sqlExecutor.ExecuteNonQuery(conn, optimization.OptimizeSql);
                log("  Optimization applied successfully.");
                optimizeApplied = true;

                sqlExecutor.UpdateStatistics(conn);

                // Phase 3: After measurement
                _currentPhase = "After";
                _phaseIterTotal = _config.BenchmarkIterations;
                log($"--- After Measurement ({_config.BenchmarkIterations} iterations) ---");
                afterResult = new BenchmarkResult { Label = "After" };
                for (int i = 1; i <= _config.BenchmarkIterations; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _phaseIterCurrent = i;
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
                    // Write reports after after-measurement completes
                    reportGenerator.GenerateSummaryReport(runFolder, allSummaries, _config.TimingMetric, _runStartTime);
                    SafeGenerateDetailReports(reportGenerator, outputFolder, optimizationName, beforeResult, afterResult, null, benchmarkError, log);
                }

                // Phase 4: Revert
                _currentPhase = "Reverting";
                _phaseIterTotal = 0;
                log("--- Reverting Optimization ---");
                sqlExecutor.ExecuteNonQuery(conn, optimization.RevertSql);
                log("  Revert applied successfully.");

                log("  Verifying revert...");
                sqlExecutor.ClearCache(conn);
                var verifyTiming = sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                log($"  Verification timing: {verifyTiming}");
            }
        }
        catch (OperationCanceledException)
        {
            benchmarkError = "Run cancelled by user.";
            log($"  Cancelled during: {_currentPhase}");
            if (summary != null) summary.Status = "Cancelled";
            if (optimizeApplied) revertFailed = true;
            throw; // re-throw so RunAsync catches it
        }
        catch (Exception ex)
        {
            benchmarkError = ex.ToString();
            log($"  ERROR during benchmark phases: {ex.Message}");
            if (summary != null) summary.Status = "Failed";
            if (optimizeApplied) revertFailed = true;
        }

        // Generate final per-optimization reports
        _currentPhase = "Reporting";
        _phaseIterTotal = 0;
        log("--- Generating Reports ---");
        var finalAiResults = aiResults ?? summary?.AiAttempts;
        SafeGenerateDetailReports(reportGenerator, outputFolder, optimizationName, beforeResult, afterResult, finalAiResults, benchmarkError, log);

        log($"\nDone processing {optimization.Name}.");
        return revertFailed;
    }

    private void SafeGenerateDetailReports(
        ReportGenerator reportGenerator,
        string outputFolder,
        string optimizationName,
        BenchmarkResult beforeResult,
        BenchmarkResult? afterResult,
        List<AiOptimizer.AiOptimizationResult>? aiResults,
        string? benchmarkError,
        Action<string> log)
    {
        try
        {
            reportGenerator.GenerateMarkdownReport(outputFolder, optimizationName, beforeResult, afterResult, _config.TimingMetric, aiResults, benchmarkError);
            reportGenerator.GenerateHtmlReport(outputFolder, optimizationName, beforeResult, afterResult, _config.TimingMetric, aiResults, benchmarkError);
        }
        catch (Exception ex)
        {
            log($"  ERROR generating reports: {ex.Message}");
        }
    }
}
