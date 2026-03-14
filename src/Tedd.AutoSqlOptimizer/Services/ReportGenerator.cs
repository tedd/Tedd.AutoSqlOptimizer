using Tedd.AutoSqlOptimizer.Models;

using System.Text;

using static Tedd.AutoSqlOptimizer.Services.AiOptimizer;

namespace Tedd.AutoSqlOptimizer.Services;

public class ReportGenerator
{
    private readonly Action<string> _log;

    public ReportGenerator(Action<string> log)
    {
        _log = log;
    }

    public void GenerateMarkdownReport(
        string outputFolder,
        string optimizationName,
        BenchmarkResult beforeResult,
        BenchmarkResult? afterResult,
        string timingMetric,
        List<AiOptimizationResult>? aiResults = null,
        string? errorMessage = null)
    {
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "avg" : "min";
        var sb = new StringBuilder();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        sb.AppendLine($"# Benchmark Results — {timestamp}");
        sb.AppendLine();
        sb.AppendLine($"## {optimizationName}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(errorMessage))
        {
            sb.AppendLine($"> [!CAUTION]");
            sb.AppendLine($"> **Benchmark failed with error:**");
            sb.AppendLine($"> {errorMessage}");
            sb.AppendLine();
        }

        // Before results table
        AppendResultSection(sb, "Before Optimization", beforeResult);

        if (afterResult != null)
        {
            AppendResultSection(sb, "After Optimization", afterResult);
            AppendImprovementTable(sb, beforeResult, afterResult, timingMetric);
        }

        // AI optimization results
        if (aiResults != null && aiResults.Count > 0)
        {
            sb.AppendLine("### AI Optimization Results");
            sb.AppendLine();

            var headerCpu = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg CPU (ms)" : "Min CPU (ms)";
            var headerElapsed = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg Elapsed (ms)" : "Min Elapsed (ms)";
            sb.AppendLine($"| # | Description | Optimize | Revert | {headerCpu} | {headerElapsed} | CPU Δ | Elapsed Δ |");
            sb.AppendLine("|---|-------------|----------|--------|-------------|-------------------|-------|-----------|");

            foreach (var ai in aiResults)
            {
                var valCpu = ai.AfterResult?.GetCpuValue(timingMetric).ToString("F0") ?? "—";
                var valElapsed = ai.AfterResult?.GetElapsedValue(timingMetric).ToString("F0") ?? "—";

                var beforeCpu = beforeResult.GetCpuValue(timingMetric);
                var beforeElapsed = beforeResult.GetElapsedValue(timingMetric);

                var cpuDelta = ai.AfterResult != null && beforeCpu > 0
                    ? $"{(1 - ai.AfterResult.GetCpuValue(timingMetric) / beforeCpu) * 100:F1}%"
                    : "—";
                var elapsedDelta = ai.AfterResult != null && beforeElapsed > 0
                    ? $"{(1 - ai.AfterResult.GetElapsedValue(timingMetric) / beforeElapsed) * 100:F1}%"
                    : "—";
                var optStatus = ai.OptimizeSucceeded ? "✅" : "❌";
                var revStatus = ai.RevertSucceeded ? "✅" : "❌";

                sb.AppendLine($"| {ai.Name} | {ai.Description} | {optStatus} ({ai.OptimizeAttempts}) | {revStatus} ({ai.RevertAttempts}) | {valCpu} | {valElapsed} | {cpuDelta} | {elapsedDelta} |");
            }

            sb.AppendLine();

            // Detailed per-AI-opt results
            foreach (var ai in aiResults.Where(a => a.AfterResult != null))
            {
                sb.AppendLine($"#### {ai.Name}: {ai.Description}");
                sb.AppendLine();
                AppendResultSection(sb, $"{ai.Name} — After", ai.AfterResult!);
                AppendImprovementTable(sb, beforeResult, ai.AfterResult!, timingMetric);
            }
        }

        var reportPath = Path.Combine(outputFolder, "results.md");
        File.WriteAllText(reportPath, sb.ToString());
        _log($"Markdown report written to {reportPath}");
    }

    public void GenerateHtmlReport(
        string outputFolder,
        string optimizationName,
        BenchmarkResult beforeResult,
        BenchmarkResult? afterResult,
        string timingMetric,
        List<AiOptimizationResult>? aiResults = null,
        string? errorMessage = null)
    {
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>Benchmark Results — {optimizationName}</title>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/chart.js@4\"></script>");
        sb.AppendLine("  <style>");
        sb.AppendLine(@"
    body { font-family: 'Segoe UI', Tahoma, sans-serif; margin: 20px; background: #f8f9fa; color: #333; }
    h1 { color: #2c3e50; border-bottom: 3px solid #3498db; padding-bottom: 10px; }
    h2 { color: #2980b9; }
    h3 { color: #7f8c8d; }
    .chart-container { max-width: 800px; margin: 20px auto; background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
    table { border-collapse: collapse; width: 100%; max-width: 800px; margin: 10px auto; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
    th, td { padding: 10px 14px; text-align: right; border-bottom: 1px solid #ecf0f1; }
    th { background: #3498db; color: white; font-weight: 600; }
    td:first-child, th:first-child { text-align: left; }
    tr:hover { background: #f1f8ff; }
    .summary { max-width: 800px; margin: 20px auto; padding: 15px; background: #e8f8f5; border-left: 4px solid #1abc9c; border-radius: 4px; }
    .improvement { color: #27ae60; font-weight: bold; }
    .regression { color: #e74c3c; font-weight: bold; }
    .status-ok { color: #27ae60; }
    .status-fail { color: #e74c3c; }
");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"  <h1>Benchmark Results — {optimizationName}</h1>");
        sb.AppendLine($"  <p>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

        if (!string.IsNullOrEmpty(errorMessage))
        {
            sb.AppendLine("  <div class=\"regression\" style=\"padding: 15px; border: 2px solid #e74c3c; border-radius: 8px; margin-bottom: 20px; background: #fdf2f2;\">");
            sb.AppendLine("    <strong>ERROR:</strong> benchmark execution failed.<br>");
            sb.AppendLine($"    <pre style=\"white-space: pre-wrap; margin-top: 10px;\">{System.Net.WebUtility.HtmlEncode(errorMessage)}</pre>");
            sb.AppendLine("  </div>");
        }

        // Summary stats
        sb.AppendLine("  <h2>Summary</h2>");
        sb.AppendLine("  <div class=\"summary\">");

        var beforeCpu = beforeResult.GetCpuValue(timingMetric);
        var beforeElapsed = beforeResult.GetElapsedValue(timingMetric);

        sb.AppendLine($"    <p><strong>Before:</strong> {metricLabel} CPU={beforeCpu:F0}ms, {metricLabel} Elapsed={beforeElapsed:F0}ms</p>");
        if (afterResult != null)
        {
            var afterCpu = afterResult.GetCpuValue(timingMetric);
            var afterElapsed = afterResult.GetElapsedValue(timingMetric);

            sb.AppendLine($"    <p><strong>After:</strong> {metricLabel} CPU={afterCpu:F0}ms, {metricLabel} Elapsed={afterElapsed:F0}ms</p>");
            var cpuPct = beforeCpu > 0 ? (1 - afterCpu / beforeCpu) * 100 : 0;
            var elapsedPct = beforeElapsed > 0 ? (1 - afterElapsed / beforeElapsed) * 100 : 0;
            var cpuClass = cpuPct > 0 ? "improvement" : "regression";
            var elapsedClass = elapsedPct > 0 ? "improvement" : "regression";
            sb.AppendLine($"    <p>CPU: <span class=\"{cpuClass}\">{cpuPct:F1}%</span> | Elapsed: <span class=\"{elapsedClass}\">{elapsedPct:F1}%</span></p>");
        }
        sb.AppendLine("  </div>");

        // Before/After comparison chart
        var chartId = 0;

        // Per-iteration line chart (Before)
        sb.AppendLine("  <h2>Before — Per-Iteration Timings</h2>");
        RenderLineChart(sb, $"chart{chartId++}", "Before — Iteration Timings", beforeResult);

        if (afterResult != null)
        {
            sb.AppendLine("  <h2>After — Per-Iteration Timings</h2>");
            RenderLineChart(sb, $"chart{chartId++}", "After — Iteration Timings", afterResult);

            // Bar chart comparison
            sb.AppendLine("  <h2>Before vs After Comparison</h2>");
            RenderComparisonBarChart(sb, $"chart{chartId++}", beforeResult, afterResult, timingMetric);
        }

        // AI results
        if (aiResults != null && aiResults.Count > 0)
        {
            sb.AppendLine("  <h2>AI Optimization Results</h2>");

            // Summary table
            sb.AppendLine("  <table>");
            var headerCpu = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg CPU" : "Min CPU";
            var headerElapsed = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg Elapsed" : "Min Elapsed";
            sb.AppendLine($"    <tr><th>#</th><th>Description</th><th>Optimize</th><th>Revert</th><th>{headerCpu}</th><th>{headerElapsed}</th><th>CPU Δ</th><th>Elapsed Δ</th></tr>");
            foreach (var ai in aiResults)
            {
                var valCpu = ai.AfterResult?.GetCpuValue(timingMetric).ToString("F0") ?? "—";
                var valElapsed = ai.AfterResult?.GetElapsedValue(timingMetric).ToString("F0") ?? "—";

                beforeCpu = beforeResult.GetCpuValue(timingMetric);
                beforeElapsed = beforeResult.GetElapsedValue(timingMetric);

                var cpuDelta = ai.AfterResult != null && beforeCpu > 0
                    ? $"{(1 - ai.AfterResult.GetCpuValue(timingMetric) / beforeCpu) * 100:F1}%" : "—";
                var elapsedDelta = ai.AfterResult != null && beforeElapsed > 0
                    ? $"{(1 - ai.AfterResult.GetElapsedValue(timingMetric) / beforeElapsed) * 100:F1}%" : "—";
                var optClass = ai.OptimizeSucceeded ? "status-ok" : "status-fail";
                var revClass = ai.RevertSucceeded ? "status-ok" : "status-fail";

                sb.AppendLine($"    <tr><td>{ai.Name}</td><td>{ai.Description}</td>" +
                    $"<td class=\"{optClass}\">{(ai.OptimizeSucceeded ? "✅" : "❌")} ({ai.OptimizeAttempts})</td>" +
                    $"<td class=\"{revClass}\">{(ai.RevertSucceeded ? "✅" : "❌")} ({ai.RevertAttempts})</td>" +
                    $"<td>{valCpu}ms</td><td>{valElapsed}ms</td><td>{cpuDelta}</td><td>{elapsedDelta}</td></tr>");
            }
            sb.AppendLine("  </table>");

            // AI comparison chart
            var successfulAi = aiResults.Where(a => a.AfterResult != null).ToList();
            if (successfulAi.Count > 0)
            {
                sb.AppendLine("  <h3>AI Optimization Comparison</h3>");
                RenderAiComparisonChart(sb, $"chart{chartId++}", beforeResult, successfulAi, timingMetric);

                // Per-AI-opt line charts
                foreach (var ai in successfulAi)
                {
                    sb.AppendLine($"  <h3>{ai.Name}: {ai.Description}</h3>");
                    RenderLineChart(sb, $"chart{chartId++}", $"{ai.Name} — Iteration Timings", ai.AfterResult!);
                }
            }
        }

        // Raw data tables
        sb.AppendLine("  <h2>Raw Data</h2>");
        RenderDataTable(sb, "Before", beforeResult);
        if (afterResult != null)
            RenderDataTable(sb, "After", afterResult);
        if (aiResults != null)
        {
            foreach (var ai in aiResults.Where(a => a.AfterResult != null))
                RenderDataTable(sb, ai.Name, ai.AfterResult!);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        var reportPath = Path.Combine(outputFolder, "results.html");
        File.WriteAllText(reportPath, sb.ToString());
        _log($"HTML report written to {reportPath}");
    }

    public void GenerateSummaryReport(string runFolder, List<OptimizationSummary> summaries, string timingMetric)
    {
        GenerateSummaryMarkdown(runFolder, summaries, timingMetric);
        GenerateSummaryHtml(runFolder, summaries, timingMetric);
    }

    private void GenerateSummaryMarkdown(string runFolder, List<OptimizationSummary> summaries, string timingMetric)
    {
        var sb = new StringBuilder();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        sb.AppendLine($"# Benchmark Run Summary — {timestamp}");
        sb.AppendLine();
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";
        sb.AppendLine($"| Optimization | Status | Before ({metricLabel}) | Best After ({metricLabel}) | Imp. | Best Strategy |");
        sb.AppendLine("|--------------|--------|--------------|------------------|------|---------------|");

        foreach (var s in summaries)
        {
            var before = s.BeforeElapsed.HasValue ? $"{s.BeforeElapsed:F0}ms" : "—";
            var after = s.BestAfterElapsed.HasValue ? $"{s.BestAfterElapsed:F0}ms" : "—";
            var imp = s.BeforeElapsed.HasValue && s.BestAfterElapsed.HasValue && s.BeforeElapsed > 0
                ? $"{(1 - s.BestAfterElapsed.Value / s.BeforeElapsed.Value) * 100:F1}%"
                : "—";

            sb.AppendLine($"| {s.FolderName} | {s.Status} | {before} | {after} | {imp} | {s.BestStrategy} |");
        }

        var reportPath = Path.Combine(runFolder, "summary.md");
        File.WriteAllText(reportPath, sb.ToString());
    }

    private void GenerateSummaryHtml(string runFolder, List<OptimizationSummary> summaries, string timingMetric)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>Benchmark Overall Summary</title>");
        sb.AppendLine("  <meta http-equiv=\"refresh\" content=\"10\">"); // Auto-refresh every 10 seconds
        sb.AppendLine("  <style>");
        sb.AppendLine(@"
    body { font-family: 'Segoe UI', Tahoma, sans-serif; margin: 20px; background: #f8f9fa; color: #333; }
    h1 { color: #2c3e50; border-bottom: 3px solid #3498db; padding-bottom: 10px; }
    table { border-collapse: collapse; width: 100%; margin: 20px 0; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
    th, td { padding: 12px 15px; text-align: left; border-bottom: 1px solid #ecf0f1; }
    th { background: #3498db; color: white; }
    .status-Running { color: #3498db; font-weight: bold; }
    .status-Done { color: #27ae60; }
    .status-Failed { color: #e74c3c; }
    .improvement { color: #27ae60; font-weight: bold; }
    .regression { color: #e74c3c; font-weight: bold; }
");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Benchmark Overall Summary</h1>");
        sb.AppendLine($"  <p>Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Auto-refreshes every 10s)</p>");

        sb.AppendLine("  <table>");
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Average" : "Lowest";
        sb.AppendLine($"    <tr><th>Optimization</th><th>Status</th><th>Before ({metricLabel})</th><th>Best After ({metricLabel})</th><th>Improvement</th><th>Best Strategy</th></tr>");

        foreach (var s in summaries)
        {
            var before = s.BeforeElapsed.HasValue ? $"{s.BeforeElapsed:F0}ms" : "—";
            var after = s.BestAfterElapsed.HasValue ? $"{s.BestAfterElapsed:F0}ms" : "—";
            var imp = "—";
            var impClass = "";

            if (s.BeforeElapsed.HasValue && s.BestAfterElapsed.HasValue && s.BeforeElapsed > 0)
            {
                var val = (1 - s.BestAfterElapsed.Value / s.BeforeElapsed.Value) * 100;
                imp = $"{val:F1}%";
                impClass = val > 0 ? "improvement" : "regression";
            }

            sb.AppendLine($"    <tr>");
            sb.AppendLine($"      <td><strong>{s.FolderName}</strong></td>");
            sb.AppendLine($"      <td class=\"status-{s.Status}\">{s.Status}</td>");
            sb.AppendLine($"      <td>{before}</td>");
            sb.AppendLine($"      <td>{after}</td>");
            sb.AppendLine($"      <td class=\"{impClass}\">{imp}</td>");
            sb.AppendLine($"      <td>{s.BestStrategy}</td>");
            sb.AppendLine($"    </tr>");
        }

        sb.AppendLine("  </table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        var reportPath = Path.Combine(runFolder, "summary.html");
        File.WriteAllText(reportPath, sb.ToString());
    }

    private static void AppendResultSection(StringBuilder sb, string label, BenchmarkResult result)
    {
        sb.AppendLine($"### {label}");
        sb.AppendLine();
        sb.AppendLine("| Run | CPU (ms) | Elapsed (ms) |");
        sb.AppendLine("|-----|----------|--------------|");
        for (int i = 0; i < result.Timings.Count; i++)
        {
            sb.AppendLine($"| {i + 1} | {result.Timings[i].CpuTimeMs} | {result.Timings[i].ElapsedTimeMs} |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Summary**: Min CPU={result.MinCpu}ms, Max CPU={result.MaxCpu}ms, " +
            $"Avg CPU={result.AvgCpu:F0}ms, Median CPU={result.MedianCpu}ms | " +
            $"Min Elapsed={result.MinElapsed}ms, Max Elapsed={result.MaxElapsed}ms, " +
            $"Avg Elapsed={result.AvgElapsed:F0}ms, Median Elapsed={result.MedianElapsed}ms");
        sb.AppendLine();
    }

    private static void AppendImprovementTable(StringBuilder sb, BenchmarkResult before, BenchmarkResult after, string timingMetric)
    {
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "avg" : "min";
        sb.AppendLine("### Improvement");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Before ({metricLabel}) | After ({metricLabel}) | Improvement |");
        sb.AppendLine("|--------|-------------|-------------|-------------|");

        var beforeCpu = before.GetCpuValue(timingMetric);
        var afterCpu = after.GetCpuValue(timingMetric);
        var beforeElapsed = before.GetElapsedValue(timingMetric);
        var afterElapsed = after.GetElapsedValue(timingMetric);

        var cpuPct = beforeCpu > 0 ? (1 - afterCpu / beforeCpu) * 100 : 0;
        var elapsedPct = beforeElapsed > 0 ? (1 - afterElapsed / beforeElapsed) * 100 : 0;

        sb.AppendLine($"| CPU Time | {beforeCpu:F0} ms | {afterCpu:F0} ms | {cpuPct:F1}% faster |");
        sb.AppendLine($"| Elapsed Time | {beforeElapsed:F0} ms | {afterElapsed:F0} ms | {elapsedPct:F1}% faster |");
        sb.AppendLine();
    }

    private static void RenderLineChart(StringBuilder sb, string canvasId, string title, BenchmarkResult result)
    {
        var cpuData = string.Join(",", result.Timings.Select(t => t.CpuTimeMs));
        var elapsedData = string.Join(",", result.Timings.Select(t => t.ElapsedTimeMs));
        var labels = string.Join(",", Enumerable.Range(1, result.Timings.Count).Select(i => $"'{i}'"));

        sb.AppendLine("  <div class=\"chart-container\">");
        sb.AppendLine($"    <canvas id=\"{canvasId}\"></canvas>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine($"  new Chart(document.getElementById('{canvasId}'), {{");
        sb.AppendLine("    type: 'line',");
        sb.AppendLine($"    data: {{ labels: [{labels}], datasets: [");
        sb.AppendLine($"      {{ label: 'CPU Time (ms)', data: [{cpuData}], borderColor: '#e74c3c', backgroundColor: 'rgba(231,76,60,0.1)', fill: true, tension: 0.3 }},");
        sb.AppendLine($"      {{ label: 'Elapsed Time (ms)', data: [{elapsedData}], borderColor: '#3498db', backgroundColor: 'rgba(52,152,219,0.1)', fill: true, tension: 0.3 }}");
        sb.AppendLine("    ]},");
        sb.AppendLine($"    options: {{ responsive: true, plugins: {{ title: {{ display: true, text: '{title}' }} }} }}");
        sb.AppendLine("  });");
        sb.AppendLine("  </script>");
    }

    private static void RenderComparisonBarChart(StringBuilder sb, string canvasId,
        BenchmarkResult before, BenchmarkResult after, string timingMetric)
    {
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";
        var beforeCpu = before.GetCpuValue(timingMetric);
        var beforeElapsed = before.GetElapsedValue(timingMetric);
        var afterCpu = after.GetCpuValue(timingMetric);
        var afterElapsed = after.GetElapsedValue(timingMetric);

        sb.AppendLine("  <div class=\"chart-container\">");
        sb.AppendLine($"    <canvas id=\"{canvasId}\"></canvas>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine($"  new Chart(document.getElementById('{canvasId}'), {{");
        sb.AppendLine("    type: 'bar',");
        sb.AppendLine($"    data: {{ labels: ['{metricLabel} CPU', 'Median CPU', '{metricLabel} Elapsed', 'Median Elapsed'], datasets: [");
        sb.AppendLine($"      {{ label: 'Before', data: [{beforeCpu:F0},{before.MedianCpu},{beforeElapsed:F0},{before.MedianElapsed}], backgroundColor: 'rgba(231,76,60,0.7)' }},");
        sb.AppendLine($"      {{ label: 'After', data: [{afterCpu:F0},{after.MedianCpu},{afterElapsed:F0},{after.MedianElapsed}], backgroundColor: 'rgba(46,204,113,0.7)' }}");
        sb.AppendLine("    ]},");
        sb.AppendLine("    options: { responsive: true, plugins: { title: { display: true, text: 'Before vs After' } } }");
        sb.AppendLine("  });");
        sb.AppendLine("  </script>");
    }

    private static void RenderAiComparisonChart(StringBuilder sb, string canvasId,
        BenchmarkResult before, List<AiOptimizationResult> aiResults, string timingMetric)
    {
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg" : "Min";
        var beforeCpu = before.GetCpuValue(timingMetric);
        var beforeElapsed = before.GetElapsedValue(timingMetric);

        var labels = new List<string> { "'Baseline'" };
        var cpuData = new List<string> { $"{beforeCpu:F0}" };
        var elapsedData = new List<string> { $"{beforeElapsed:F0}" };

        foreach (var ai in aiResults)
        {
            labels.Add($"'{ai.Name}'");
            cpuData.Add($"{ai.AfterResult!.GetCpuValue(timingMetric):F0}");
            elapsedData.Add($"{ai.AfterResult!.GetElapsedValue(timingMetric):F0}");
        }

        sb.AppendLine("  <div class=\"chart-container\">");
        sb.AppendLine($"    <canvas id=\"{canvasId}\"></canvas>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine($"  new Chart(document.getElementById('{canvasId}'), {{");
        sb.AppendLine("    type: 'bar',");
        sb.AppendLine($"    data: {{ labels: [{string.Join(",", labels)}], datasets: [");
        sb.AppendLine($"      {{ label: '{metricLabel} CPU (ms)', data: [{string.Join(",", cpuData)}], backgroundColor: 'rgba(231,76,60,0.7)' }},");
        sb.AppendLine($"      {{ label: '{metricLabel} Elapsed (ms)', data: [{string.Join(",", elapsedData)}], backgroundColor: 'rgba(52,152,219,0.7)' }}");
        sb.AppendLine("    ]},");
        sb.AppendLine("    options: { responsive: true, plugins: { title: { display: true, text: 'AI Optimization Comparison' } } }");
        sb.AppendLine("  });");
        sb.AppendLine("  </script>");
    }

    private static void RenderDataTable(StringBuilder sb, string label, BenchmarkResult result)
    {
        sb.AppendLine($"  <h3>{label}</h3>");
        sb.AppendLine("  <table>");
        sb.AppendLine("    <tr><th>Run</th><th>CPU (ms)</th><th>Elapsed (ms)</th></tr>");
        for (int i = 0; i < result.Timings.Count; i++)
        {
            sb.AppendLine($"    <tr><td>{i + 1}</td><td>{result.Timings[i].CpuTimeMs}</td><td>{result.Timings[i].ElapsedTimeMs}</td></tr>");
        }
        sb.AppendLine($"    <tr style=\"font-weight:bold\"><td>Avg</td><td>{result.AvgCpu:F0}</td><td>{result.AvgElapsed:F0}</td></tr>");
        sb.AppendLine($"    <tr style=\"font-weight:bold\"><td>Median</td><td>{result.MedianCpu}</td><td>{result.MedianElapsed}</td></tr>");
        sb.AppendLine($"    <tr style=\"font-weight:bold\"><td>Min</td><td>{result.MinCpu}</td><td>{result.MinElapsed}</td></tr>");
        sb.AppendLine($"    <tr style=\"font-weight:bold\"><td>Max</td><td>{result.MaxCpu}</td><td>{result.MaxElapsed}</td></tr>");
        sb.AppendLine("  </table>");
    }
}
