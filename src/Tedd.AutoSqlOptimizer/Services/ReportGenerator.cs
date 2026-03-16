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

    // ───────────────────────────────────────────────────
    //  MARKDOWN REPORTS (unchanged logic, kept for compatibility)
    // ───────────────────────────────────────────────────

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

        AppendResultSection(sb, "Before Optimization", beforeResult);

        if (afterResult != null)
        {
            AppendResultSection(sb, "After Optimization", afterResult);
            AppendImprovementTable(sb, beforeResult, afterResult, timingMetric);
        }

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

                sb.AppendLine($"| {ai.Name} | {ai.Description} | {(ai.OptimizeSucceeded ? "OK" : "FAIL")} ({ai.OptimizeAttempts}) | {(ai.RevertSucceeded ? "OK" : "FAIL")} ({ai.RevertAttempts}) | {valCpu} | {valElapsed} | {cpuDelta} | {elapsedDelta} |");
            }

            sb.AppendLine();

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

    // ───────────────────────────────────────────────────
    //  INDIVIDUAL RUN HTML REPORT
    // ───────────────────────────────────────────────────

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
        sb.AppendLine($"  <title>{He(optimizationName)} — Benchmark Results</title>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/apexcharts\"></script>");
        sb.AppendLine("  <link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css\">");
        sb.AppendLine("  <script src=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js\"></script>");
        sb.AppendLine("  <script src=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/sql.min.js\"></script>");
        sb.AppendLine($"  <style>{GetDetailPageCss()}</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("<header>");
        sb.AppendLine("  <div class=\"header-content\">");
        sb.AppendLine($"    <h1>{He(optimizationName)}</h1>");
        sb.AppendLine($"    <p class=\"subtitle\">Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} &nbsp;|&nbsp; <a href=\"summary.html\" class=\"back-link\">Back to Summary</a></p>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</header>");

        sb.AppendLine("<main>");

        if (!string.IsNullOrEmpty(errorMessage))
        {
            sb.AppendLine("<div class=\"alert alert-error\">");
            sb.AppendLine($"  <strong>Error:</strong> benchmark execution failed.<br><pre>{He(errorMessage)}</pre>");
            sb.AppendLine("</div>");
        }

        // ── Best Results Hero ──
        var beforeCpuVal = beforeResult.GetCpuValue(timingMetric);
        var beforeElapsedVal = beforeResult.GetElapsedValue(timingMetric);

        // Find the absolute best result across manual + AI
        BenchmarkResult? bestResult = afterResult;
        string bestLabel = "Manual";
        if (aiResults != null && aiResults.Count > 0)
        {
            var bestAi = aiResults
                .Where(a => a.AfterResult != null && a.OptimizeSucceeded)
                .OrderBy(a => a.AfterResult!.GetElapsedValue(timingMetric))
                .FirstOrDefault();
            if (bestAi != null)
            {
                if (bestResult == null || bestAi.AfterResult!.GetElapsedValue(timingMetric) < bestResult.GetElapsedValue(timingMetric))
                {
                    bestResult = bestAi.AfterResult;
                    bestLabel = bestAi.Name;
                }
            }
        }

        sb.AppendLine("<section class=\"hero-results\">");
        sb.AppendLine("  <h2>Best Results</h2>");
        sb.AppendLine("  <div class=\"kpi-grid\">");

        // Before KPIs
        sb.AppendLine("    <div class=\"kpi-card\">");
        sb.AppendLine($"      <div class=\"kpi-value\">{beforeElapsedVal:F0}<span class=\"kpi-unit\">ms</span></div>");
        sb.AppendLine($"      <div class=\"kpi-label\">Before ({metricLabel} Elapsed)</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"kpi-card\">");
        sb.AppendLine($"      <div class=\"kpi-value\">{beforeCpuVal:F0}<span class=\"kpi-unit\">ms</span></div>");
        sb.AppendLine($"      <div class=\"kpi-label\">Before ({metricLabel} CPU)</div>");
        sb.AppendLine("    </div>");

        if (bestResult != null)
        {
            var bestElapsed = bestResult.GetElapsedValue(timingMetric);
            var bestCpu = bestResult.GetCpuValue(timingMetric);
            var elapsedPct = beforeElapsedVal > 0 ? (1 - bestElapsed / beforeElapsedVal) * 100 : 0;
            var cpuPct = beforeCpuVal > 0 ? (1 - bestCpu / beforeCpuVal) * 100 : 0;
            var elapsedClass = elapsedPct > 0 ? "kpi-card kpi-good" : "kpi-card kpi-bad";
            var cpuClass = cpuPct > 0 ? "kpi-card kpi-good" : "kpi-card kpi-bad";

            sb.AppendLine($"    <div class=\"{elapsedClass}\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{bestElapsed:F0}<span class=\"kpi-unit\">ms</span></div>");
            sb.AppendLine($"      <div class=\"kpi-label\">Best After ({metricLabel} Elapsed)</div>");
            sb.AppendLine($"      <div class=\"kpi-delta\">{(elapsedPct >= 0 ? "+" : "")}{elapsedPct:F1}% improvement</div>");
            sb.AppendLine("    </div>");
            sb.AppendLine($"    <div class=\"{cpuClass}\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{bestCpu:F0}<span class=\"kpi-unit\">ms</span></div>");
            sb.AppendLine($"      <div class=\"kpi-label\">Best After ({metricLabel} CPU)</div>");
            sb.AppendLine($"      <div class=\"kpi-delta\">{(cpuPct >= 0 ? "+" : "")}{cpuPct:F1}% improvement</div>");
            sb.AppendLine("    </div>");

            sb.AppendLine($"    <div class=\"kpi-card kpi-highlight\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{bestLabel}</div>");
            sb.AppendLine($"      <div class=\"kpi-label\">Best Strategy</div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>"); // kpi-grid
        sb.AppendLine("</section>");

        // ── Statistics ──
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("  <h2>Run Statistics</h2>");
        var totalAttempts = (aiResults?.Count ?? 0) + (afterResult != null ? 1 : 0);
        var successfulAttempts = (aiResults?.Count(a => a.OptimizeSucceeded && a.AfterResult != null) ?? 0) + (afterResult != null ? 1 : 0);
        var aiCount = aiResults?.Count ?? 0;
        var manualCount = afterResult != null ? 1 : 0;
        sb.AppendLine("  <div class=\"stats-grid\">");
        sb.AppendLine($"    <div class=\"stat\"><span class=\"stat-num\">{totalAttempts}</span><span class=\"stat-label\">Total Attempts</span></div>");
        sb.AppendLine($"    <div class=\"stat\"><span class=\"stat-num\">{successfulAttempts}</span><span class=\"stat-label\">Successful</span></div>");
        sb.AppendLine($"    <div class=\"stat\"><span class=\"stat-num\">{aiCount}</span><span class=\"stat-label\">AI Optimizations</span></div>");
        sb.AppendLine($"    <div class=\"stat\"><span class=\"stat-num\">{manualCount}</span><span class=\"stat-label\">Manual Tests</span></div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</section>");

        // ── AI Summary (narrative) ──
        if (aiResults != null && aiResults.Count > 0)
        {
            var successfulAi = aiResults.Where(a => a.AfterResult != null && a.OptimizeSucceeded).ToList();
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>AI Optimization Analysis</h2>");
            sb.AppendLine("  <div class=\"ai-narrative\">");

            if (successfulAi.Count > 0)
            {
                var bestAi = successfulAi.OrderBy(a => a.AfterResult!.GetElapsedValue(timingMetric)).First();
                var bestElapsedPct = beforeElapsedVal > 0
                    ? (1 - bestAi.AfterResult!.GetElapsedValue(timingMetric) / beforeElapsedVal) * 100 : 0;

                sb.AppendLine($"    <p>The AI ran <strong>{aiResults.Count} optimization attempt(s)</strong>, of which <strong>{successfulAi.Count}</strong> applied successfully.</p>");
                sb.AppendLine($"    <p>The best performing optimization was <strong>{He(bestAi.Name)}</strong>: <em>{He(bestAi.Description)}</em>, achieving a <strong>{bestElapsedPct:F1}%</strong> elapsed time improvement.</p>");

                // Run-by-run narrative
                sb.AppendLine("    <h3>Iteration-by-Iteration Progression</h3>");
                sb.AppendLine("    <ol class=\"progression\">");
                foreach (var ai in aiResults)
                {
                    var statusIcon = ai.OptimizeSucceeded ? "<span class=\"badge badge-ok\">OK</span>" : "<span class=\"badge badge-fail\">FAIL</span>";
                    if (ai.AfterResult != null && ai.OptimizeSucceeded)
                    {
                        var ePct = beforeElapsedVal > 0
                            ? (1 - ai.AfterResult.GetElapsedValue(timingMetric) / beforeElapsedVal) * 100 : 0;
                        var cls = ePct > 0 ? "improvement" : "regression";
                        sb.AppendLine($"      <li>{statusIcon} <strong>{He(ai.Name)}</strong>: {He(ai.Description)} &mdash; <span class=\"{cls}\">{ePct:+0.1;-0.1}% elapsed</span></li>");
                    }
                    else
                    {
                        sb.AppendLine($"      <li>{statusIcon} <strong>{He(ai.Name)}</strong>: {He(ai.Description)} &mdash; {(ai.OptimizeSucceeded ? "no timing data" : He(ai.ErrorMessage ?? "failed to apply"))}</li>");
                    }
                }
                sb.AppendLine("    </ol>");
            }
            else
            {
                sb.AppendLine("    <p>The AI ran optimization attempts but none applied successfully.</p>");
            }
            sb.AppendLine("  </div>");
            sb.AppendLine("</section>");
        }

        // ── Charts ──
        var chartIdx = 0;

        // AI improvement progression chart (% improvement over iterations)
        if (aiResults != null && aiResults.Count > 0)
        {
            var successfulAi = aiResults.Where(a => a.AfterResult != null && a.OptimizeSucceeded).ToList();
            if (successfulAi.Count > 0)
            {
                sb.AppendLine("<section class=\"card\">");
                sb.AppendLine("  <h2>Improvement Progression</h2>");
                sb.AppendLine($"  <div id=\"chartProgress{chartIdx}\" class=\"chart-box\"></div>");
                sb.AppendLine("  <script>");

                var labels = new List<string> { "'Baseline'" };
                var elapsedSeries = new List<string> { "0" };
                var cpuSeries = new List<string> { "0" };

                foreach (var ai in aiResults)
                {
                    labels.Add($"'{Ejs(ai.Name)}'");
                    if (ai.AfterResult != null && ai.OptimizeSucceeded)
                    {
                        var ePct = beforeElapsedVal > 0 ? (1 - ai.AfterResult.GetElapsedValue(timingMetric) / beforeElapsedVal) * 100 : 0;
                        var cPct = beforeCpuVal > 0 ? (1 - ai.AfterResult.GetCpuValue(timingMetric) / beforeCpuVal) * 100 : 0;
                        elapsedSeries.Add($"{ePct:F1}");
                        cpuSeries.Add($"{cPct:F1}");
                    }
                    else
                    {
                        elapsedSeries.Add("null");
                        cpuSeries.Add("null");
                    }
                }

                sb.AppendLine($@"  new ApexCharts(document.querySelector('#chartProgress{chartIdx}'), {{
    chart: {{ type: 'line', height: 350, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [
      {{ name: 'Elapsed Improvement %', data: [{string.Join(",", elapsedSeries)}] }},
      {{ name: 'CPU Improvement %', data: [{string.Join(",", cpuSeries)}] }}
    ],
    xaxis: {{ categories: [{string.Join(",", labels)}] }},
    yaxis: {{ title: {{ text: 'Improvement (%)' }}, labels: {{ formatter: function(v) {{ return v !== null ? v.toFixed(1) + '%' : ''; }} }} }},
    colors: ['#10b981', '#6366f1'],
    stroke: {{ width: 3, curve: 'smooth' }},
    markers: {{ size: 6 }},
    tooltip: {{ y: {{ formatter: function(v) {{ return v !== null ? v.toFixed(1) + '%' : 'N/A'; }} }} }},
    annotations: {{ yaxis: [{{ y: 0, borderColor: '#64748b', strokeDashArray: 4 }}] }},
    theme: {{ mode: 'light' }}
  }}).render();");
                sb.AppendLine("  </script>");
                sb.AppendLine("</section>");
                chartIdx++;

                // Absolute timing comparison bar chart
                sb.AppendLine("<section class=\"card\">");
                sb.AppendLine("  <h2>Timing Comparison</h2>");
                sb.AppendLine($"  <div id=\"chartCompare{chartIdx}\" class=\"chart-box\"></div>");
                sb.AppendLine("  <script>");

                var barLabels = new List<string> { "'Baseline'" };
                var barElapsed = new List<string> { $"{beforeElapsedVal:F0}" };
                var barCpu = new List<string> { $"{beforeCpuVal:F0}" };

                foreach (var ai in aiResults.Where(a => a.AfterResult != null))
                {
                    barLabels.Add($"'{Ejs(ai.Name)}'");
                    barElapsed.Add($"{ai.AfterResult!.GetElapsedValue(timingMetric):F0}");
                    barCpu.Add($"{ai.AfterResult!.GetCpuValue(timingMetric):F0}");
                }

                sb.AppendLine($@"  new ApexCharts(document.querySelector('#chartCompare{chartIdx}'), {{
    chart: {{ type: 'bar', height: 350, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [
      {{ name: '{metricLabel} Elapsed (ms)', data: [{string.Join(",", barElapsed)}] }},
      {{ name: '{metricLabel} CPU (ms)', data: [{string.Join(",", barCpu)}] }}
    ],
    xaxis: {{ categories: [{string.Join(",", barLabels)}] }},
    yaxis: {{ title: {{ text: 'Time (ms)' }} }},
    colors: ['#6366f1', '#f59e0b'],
    plotOptions: {{ bar: {{ borderRadius: 4, columnWidth: '60%' }} }},
    dataLabels: {{ enabled: true, formatter: function(v) {{ return v + 'ms'; }} }},
    theme: {{ mode: 'light' }}
  }}).render();");
                sb.AppendLine("  </script>");
                sb.AppendLine("</section>");
                chartIdx++;
            }
        }

        // Before/After line charts
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("  <h2>Before — Per-Iteration Timings</h2>");
        sb.AppendLine($"  <div id=\"chartIter{chartIdx}\" class=\"chart-box\"></div>");
        RenderApexLineChart(sb, $"chartIter{chartIdx}", beforeResult);
        sb.AppendLine("</section>");
        chartIdx++;

        if (afterResult != null)
        {
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>After (Manual) — Per-Iteration Timings</h2>");
            sb.AppendLine($"  <div id=\"chartIter{chartIdx}\" class=\"chart-box\"></div>");
            RenderApexLineChart(sb, $"chartIter{chartIdx}", afterResult);
            sb.AppendLine("</section>");
            chartIdx++;
        }

        // AI per-attempt iteration charts
        if (aiResults != null)
        {
            foreach (var ai in aiResults.Where(a => a.AfterResult != null))
            {
                sb.AppendLine("<section class=\"card\">");
                sb.AppendLine($"  <h2>{He(ai.Name)} — Per-Iteration Timings</h2>");
                sb.AppendLine($"  <p class=\"text-muted\">{He(ai.Description)}</p>");
                sb.AppendLine($"  <div id=\"chartIter{chartIdx}\" class=\"chart-box\"></div>");
                RenderApexLineChart(sb, $"chartIter{chartIdx}", ai.AfterResult!);
                sb.AppendLine("</section>");
                chartIdx++;
            }
        }

        // ── AI Results Table ──
        if (aiResults != null && aiResults.Count > 0)
        {
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>All Optimization Attempts</h2>");
            sb.AppendLine("  <div class=\"table-wrap\">");
            sb.AppendLine("  <table>");
            var headerCpu = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg CPU" : "Min CPU";
            var headerElapsed = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Avg Elapsed" : "Min Elapsed";
            sb.AppendLine($"    <thead><tr><th>#</th><th>Description</th><th>Apply</th><th>Revert</th><th>{headerCpu}</th><th>{headerElapsed}</th><th>CPU Δ</th><th>Elapsed Δ</th><th>Data OK</th><th>Details</th></tr></thead>");
            sb.AppendLine("    <tbody>");
            foreach (var ai in aiResults)
            {
                var valCpu = ai.AfterResult?.GetCpuValue(timingMetric).ToString("F0") ?? "—";
                var valElapsed = ai.AfterResult?.GetElapsedValue(timingMetric).ToString("F0") ?? "—";

                var cpuDelta = ai.AfterResult != null && beforeCpuVal > 0
                    ? $"{(1 - ai.AfterResult.GetCpuValue(timingMetric) / beforeCpuVal) * 100:F1}%" : "—";
                var elapsedDelta = ai.AfterResult != null && beforeElapsedVal > 0
                    ? $"{(1 - ai.AfterResult.GetElapsedValue(timingMetric) / beforeElapsedVal) * 100:F1}%" : "—";
                var optClass = ai.OptimizeSucceeded ? "badge badge-ok" : "badge badge-fail";
                var revClass = ai.RevertSucceeded ? "badge badge-ok" : "badge badge-fail";
                var integrityBadge = ai.DataIntegrityOk ? "<span class=\"badge badge-ok\">OK</span>" : "<span class=\"badge badge-fail\">FAIL</span>";

                // Determine row highlighting for best
                var isBest = bestResult != null && ai.AfterResult == bestResult;
                var rowClass = isBest ? " class=\"row-best\"" : "";

                var folderName = Path.GetFileName(ai.Folder);
                sb.AppendLine($"    <tr{rowClass}>");
                sb.AppendLine($"      <td>{He(ai.Name)}</td>");
                sb.AppendLine($"      <td class=\"desc-col\">{He(ai.Description)}</td>");
                sb.AppendLine($"      <td><span class=\"{optClass}\">{(ai.OptimizeSucceeded ? "OK" : "FAIL")}</span> <small>({ai.OptimizeAttempts})</small></td>");
                sb.AppendLine($"      <td><span class=\"{revClass}\">{(ai.RevertSucceeded ? "OK" : "FAIL")}</span> <small>({ai.RevertAttempts})</small></td>");
                sb.AppendLine($"      <td>{valCpu}ms</td><td>{valElapsed}ms</td>");
                sb.AppendLine($"      <td>{cpuDelta}</td><td>{elapsedDelta}</td>");
                sb.AppendLine($"      <td>{integrityBadge}</td>");
                sb.AppendLine($"      <td><a href=\"{He(folderName)}/\" class=\"link-btn\">Files</a></td>");
                sb.AppendLine("    </tr>");
            }
            sb.AppendLine("    </tbody>");
            sb.AppendLine("  </table>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</section>");
        }

        // ── Raw Data Tables ──
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("  <h2>Raw Timing Data</h2>");
        RenderHtmlDataTable(sb, "Before", beforeResult);
        if (afterResult != null)
            RenderHtmlDataTable(sb, "After (Manual)", afterResult);
        if (aiResults != null)
        {
            foreach (var ai in aiResults.Where(a => a.AfterResult != null))
                RenderHtmlDataTable(sb, ai.Name, ai.AfterResult!);
        }
        sb.AppendLine("</section>");

        // ── Technical Details: SQL files as expandable boxes ──
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("  <h2>Technical Details</h2>");

        // How to reproduce
        if (bestResult != null)
        {
            sb.AppendLine("  <div class=\"info-box\">");
            sb.AppendLine("    <h3>How to Reproduce This Result</h3>");
            sb.AppendLine("    <p>To achieve the best optimization result in practice, execute the <code>2_optimize.sql</code> file from the best-performing attempt against your database:</p>");
            sb.AppendLine("    <pre><code class=\"language-sql\">-- Execute the optimization SQL from the best attempt folder</code></pre>");
            sb.AppendLine("    <p>The optimization can be reverted by executing <code>4_revert.sql</code> from the same folder.</p>");
            sb.AppendLine("  </div>");
        }

        // Read and display SQL files from output folder
        TryRenderSqlFile(sb, outputFolder, "1_before.sql", "Benchmark Query (1_before.sql)");
        TryRenderSqlFile(sb, outputFolder, "3_after.sql", "After Query (3_after.sql)");
        if (!string.IsNullOrEmpty(TryReadFile(Path.Combine(outputFolder, "2_optimize.sql"))))
            TryRenderSqlFile(sb, outputFolder, "2_optimize.sql", "Manual Optimization (2_optimize.sql)");
        if (!string.IsNullOrEmpty(TryReadFile(Path.Combine(outputFolder, "4_revert.sql"))))
            TryRenderSqlFile(sb, outputFolder, "4_revert.sql", "Manual Revert (4_revert.sql)");

        // AI Input
        var aiInputPath = Path.Combine(outputFolder, "AI_Input.txt");
        if (File.Exists(aiInputPath))
        {
            var aiInput = File.ReadAllText(aiInputPath);
            if (!string.IsNullOrWhiteSpace(aiInput))
            {
                sb.AppendLine("  <details class=\"sql-expand\">");
                sb.AppendLine("    <summary>AI Input (AI_Input.txt)</summary>");
                sb.AppendLine($"    <pre><code>{He(aiInput)}</code></pre>");
                sb.AppendLine("  </details>");
            }
        }

        // init.sql from parent optimize folder
        var initSqlPath = Path.Combine(outputFolder, "..", "..", "Optimize", "init.sql");
        if (File.Exists(initSqlPath))
        {
            var initSql = File.ReadAllText(initSqlPath);
            if (!string.IsNullOrWhiteSpace(initSql))
            {
                sb.AppendLine("  <details class=\"sql-expand\">");
                sb.AppendLine("    <summary>Database Initialization (init.sql)</summary>");
                sb.AppendLine($"    <pre><code class=\"language-sql\">{He(initSql)}</code></pre>");
                sb.AppendLine("  </details>");
            }
        }

        // AI attempt SQL files
        if (aiResults != null)
        {
            foreach (var ai in aiResults)
            {
                var folderName = Path.GetFileName(ai.Folder);
                var aiOptFolder = Path.Combine(outputFolder, folderName);
                if (Directory.Exists(aiOptFolder))
                {
                    sb.AppendLine($"  <details class=\"sql-expand\">");
                    sb.AppendLine($"    <summary>{He(ai.Name)}: {He(ai.Description)} — SQL Files</summary>");
                    TryRenderSqlFileInline(sb, aiOptFolder, "2_optimize.sql", "Optimization SQL");
                    TryRenderSqlFileInline(sb, aiOptFolder, "4_revert.sql", "Revert SQL");
                    var descPath = Path.Combine(aiOptFolder, "description.txt");
                    if (File.Exists(descPath))
                    {
                        sb.AppendLine($"    <h4>Description</h4>");
                        sb.AppendLine($"    <pre>{He(File.ReadAllText(descPath))}</pre>");
                    }
                    var integrityPath = Path.Combine(aiOptFolder, "data_integrity_report.txt");
                    if (File.Exists(integrityPath))
                    {
                        sb.AppendLine($"    <h4>Data Integrity Report</h4>");
                        sb.AppendLine($"    <pre>{He(File.ReadAllText(integrityPath))}</pre>");
                    }
                    sb.AppendLine("  </details>");
                }
            }
        }

        sb.AppendLine("</section>");

        sb.AppendLine("</main>");
        sb.AppendLine("<script>hljs.highlightAll();</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        var reportPath = Path.Combine(outputFolder, "results.html");
        File.WriteAllText(reportPath, sb.ToString());
        _log($"HTML report written to {reportPath}");
    }

    // ───────────────────────────────────────────────────
    //  SUMMARY HTML REPORT
    // ───────────────────────────────────────────────────

    public void GenerateSummaryReport(string runFolder, List<OptimizationSummary> summaries, string timingMetric, DateTime runStartTime = default)
    {
        GenerateSummaryMarkdown(runFolder, summaries, timingMetric);
        GenerateSummaryHtml(runFolder, summaries, timingMetric, runStartTime);
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

    private void GenerateSummaryHtml(string runFolder, List<OptimizationSummary> summaries, string timingMetric, DateTime runStartTime)
    {
        var now = DateTime.Now;
        var metricLabel = timingMetric.Equals("Average", StringComparison.OrdinalIgnoreCase) ? "Average" : "Lowest";
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>SQL Auto-Optimizer — Run Summary</title>");
        sb.AppendLine("  <meta http-equiv=\"refresh\" content=\"10\">");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/apexcharts\"></script>");
        sb.AppendLine($"  <style>{GetSummaryPageCss()}</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // ── Header ──
        sb.AppendLine("<header>");
        sb.AppendLine("  <div class=\"header-content\">");
        sb.AppendLine("    <div class=\"logo\">SQL Auto-Optimizer</div>");
        sb.AppendLine("    <h1>Run Summary</h1>");
        sb.AppendLine($"    <p class=\"subtitle\">Last updated: {now:yyyy-MM-dd HH:mm:ss} &nbsp;|&nbsp; Auto-refreshes every 10s</p>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</header>");

        sb.AppendLine("<main>");

        // ── KPI Dashboard ──
        var totalRuns = summaries.Count;
        var runningCount = summaries.Count(s => s.Status == "Running");
        var doneCount = summaries.Count(s => s.Status == "Done");
        var failedCount = summaries.Count(s => s.Status is "Failed" or "Cancelled");
        var pendingCount = summaries.Count(s => s.Status == "Pending");

        var withResults = summaries.Where(s => s.BeforeElapsed.HasValue && s.BestAfterElapsed.HasValue && s.BeforeElapsed > 0).ToList();
        var bestImprovement = withResults.Count > 0
            ? withResults.Max(s => (1 - s.BestAfterElapsed!.Value / s.BeforeElapsed!.Value) * 100)
            : 0.0;

        var runDuration = runStartTime != default ? now - runStartTime : TimeSpan.Zero;
        var isRunning = runningCount > 0 || pendingCount > 0;

        sb.AppendLine("<section class=\"dashboard\">");
        sb.AppendLine("  <div class=\"kpi-grid\">");

        sb.AppendLine("    <div class=\"kpi-card\">");
        sb.AppendLine($"      <div class=\"kpi-value\">{totalRuns}</div>");
        sb.AppendLine("      <div class=\"kpi-label\">Total Optimizations</div>");
        sb.AppendLine("    </div>");

        if (isRunning)
        {
            sb.AppendLine("    <div class=\"kpi-card kpi-running\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{runningCount}</div>");
            sb.AppendLine("      <div class=\"kpi-label\">Running</div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("    <div class=\"kpi-card kpi-good\">");
        sb.AppendLine($"      <div class=\"kpi-value\">{doneCount}</div>");
        sb.AppendLine("      <div class=\"kpi-label\">Completed</div>");
        sb.AppendLine("    </div>");

        if (failedCount > 0)
        {
            sb.AppendLine("    <div class=\"kpi-card kpi-bad\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{failedCount}</div>");
            sb.AppendLine("      <div class=\"kpi-label\">Failed</div>");
            sb.AppendLine("    </div>");
        }

        if (withResults.Count > 0)
        {
            var bestClass = bestImprovement > 0 ? "kpi-card kpi-good" : "kpi-card kpi-bad";
            sb.AppendLine($"    <div class=\"{bestClass}\">");
            sb.AppendLine($"      <div class=\"kpi-value\">{bestImprovement:F1}%</div>");
            sb.AppendLine("      <div class=\"kpi-label\">Best Improvement</div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("    <div class=\"kpi-card\">");
        sb.AppendLine($"      <div class=\"kpi-value\">{FormatDuration(runDuration)}</div>");
        sb.AppendLine($"      <div class=\"kpi-label\">{(isRunning ? "Elapsed Time" : "Total Duration")}</div>");
        sb.AppendLine("    </div>");

        sb.AppendLine("  </div>"); // kpi-grid

        // Running status indicator
        if (isRunning)
        {
            var currentRunning = summaries.FirstOrDefault(s => s.Status == "Running");
            if (currentRunning != null)
            {
                var runElapsed = currentRunning.StartTime.HasValue ? now - currentRunning.StartTime.Value : TimeSpan.Zero;
                sb.AppendLine("  <div class=\"running-status\">");
                sb.AppendLine($"    <span class=\"pulse\"></span>");
                sb.AppendLine($"    Currently processing: <strong>{He(currentRunning.FolderName)}</strong>");
                sb.AppendLine($"    — running for {FormatDuration(runElapsed)}");
                sb.AppendLine($"    ({doneCount + 1} of {totalRuns})");
                sb.AppendLine("  </div>");
            }
        }

        sb.AppendLine("</section>");

        // ── Results Table ──
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("  <h2>Results</h2>");
        sb.AppendLine("  <div class=\"table-wrap\">");
        sb.AppendLine("  <table>");
        sb.AppendLine($"    <thead><tr>");
        sb.AppendLine($"      <th>#</th><th>Optimization</th><th>Status</th><th>Type</th>");
        sb.AppendLine($"      <th>Before ({metricLabel})</th><th>Best After ({metricLabel})</th>");
        sb.AppendLine($"      <th>Improvement</th><th>AI Iters</th><th>Duration</th><th>Best Strategy</th><th></th>");
        sb.AppendLine($"    </tr></thead>");
        sb.AppendLine("    <tbody>");

        for (int i = 0; i < summaries.Count; i++)
        {
            var s = summaries[i];
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

            var statusClass = s.Status switch
            {
                "Running"   => "status-running",
                "Done"      => "status-done",
                "Failed"    => "status-failed",
                "Cancelled" => "status-failed",
                _ => "status-pending"
            };

            var statusIcon = s.Status switch
            {
                "Running" => "<span class=\"pulse-sm\"></span>",
                "Done"    => "",
                "Failed"  => "",
                _ => ""
            };

            var type = s.IsManual ? "Manual" : (s.AiIterationCount > 0 || s.Status == "Running" ? "AI" : "—");
            var aiIters = s.AiIterationCount > 0 ? s.AiIterationCount.ToString() : "—";

            var duration = "—";
            if (s.Duration.HasValue)
                duration = FormatDuration(s.Duration.Value);
            else if (s.Status == "Running" && s.StartTime.HasValue)
                duration = FormatDuration(now - s.StartTime.Value) + "...";

            var linkHtml = "";
            if (s.Status is "Done" or "Failed" or "Cancelled")
            {
                var folderLink = string.IsNullOrEmpty(s.OutputFolderName) ? s.FolderName : s.OutputFolderName;
                linkHtml = $"<a href=\"{He(folderLink)}/results.html\" class=\"link-btn\">View</a>";
            }

            sb.AppendLine("    <tr>");
            sb.AppendLine($"      <td>{i + 1}</td>");
            sb.AppendLine($"      <td><strong>{He(s.FolderName)}</strong></td>");
            sb.AppendLine($"      <td><span class=\"{statusClass}\">{statusIcon}{s.Status}</span></td>");
            sb.AppendLine($"      <td>{type}</td>");
            sb.AppendLine($"      <td>{before}</td>");
            sb.AppendLine($"      <td>{after}</td>");
            sb.AppendLine($"      <td class=\"{impClass}\">{imp}</td>");
            sb.AppendLine($"      <td>{aiIters}</td>");
            sb.AppendLine($"      <td>{duration}</td>");
            sb.AppendLine($"      <td>{He(s.BestStrategy)}</td>");
            sb.AppendLine($"      <td>{linkHtml}</td>");
            sb.AppendLine("    </tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</section>");

        // ── Charts ──
        if (withResults.Count > 0)
        {
            var chartLabels = string.Join(",", withResults.Select(s => $"'{Ejs(s.FolderName)}'"));
            var beforeElapsedData = string.Join(",", withResults.Select(s => $"{s.BeforeElapsed:F0}"));
            var afterElapsedData = string.Join(",", withResults.Select(s => $"{s.BestAfterElapsed:F0}"));
            var beforeCpuData = string.Join(",", withResults.Select(s => $"{s.BeforeCpu:F0}"));
            var afterCpuData = string.Join(",", withResults.Select(s => $"{s.BestAfterCpu:F0}"));
            var improvementData = string.Join(",", withResults.Select(s =>
                s.BeforeElapsed > 0 ? $"{(1 - s.BestAfterElapsed!.Value / s.BeforeElapsed!.Value) * 100:F1}" : "0"));

            // Chart 1: Improvement % per optimization
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>Elapsed Time Improvement %</h2>");
            sb.AppendLine("  <div id=\"chartImprovement\" class=\"chart-box\"></div>");
            sb.AppendLine("  <script>");
            sb.AppendLine($@"  new ApexCharts(document.querySelector('#chartImprovement'), {{
    chart: {{ type: 'bar', height: 350, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [{{ name: 'Improvement %', data: [{improvementData}] }}],
    xaxis: {{ categories: [{chartLabels}], labels: {{ rotate: -45, style: {{ fontSize: '11px' }} }} }},
    yaxis: {{ title: {{ text: 'Improvement (%)' }}, labels: {{ formatter: function(v) {{ return v.toFixed(1) + '%'; }} }} }},
    colors: [{string.Join(",", withResults.Select(s =>
                s.BeforeElapsed > 0 && (1 - s.BestAfterElapsed!.Value / s.BeforeElapsed!.Value) >= 0
                    ? "'#10b981'" : "'#ef4444'"))}],
    plotOptions: {{ bar: {{ borderRadius: 6, columnWidth: '55%', distributed: true }} }},
    dataLabels: {{ enabled: true, formatter: function(v) {{ return v.toFixed(1) + '%'; }}, style: {{ fontSize: '13px' }} }},
    legend: {{ show: false }},
    tooltip: {{ y: {{ formatter: function(v) {{ return v.toFixed(1) + '% improvement'; }} }} }},
    theme: {{ mode: 'light' }}
  }}).render();");
            sb.AppendLine("  </script>");
            sb.AppendLine("</section>");

            // Chart 2: Before vs Best After — Elapsed Time
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>Before vs Best After — Elapsed Time</h2>");
            sb.AppendLine("  <div id=\"chartElapsed\" class=\"chart-box\"></div>");
            sb.AppendLine("  <script>");
            sb.AppendLine($@"  new ApexCharts(document.querySelector('#chartElapsed'), {{
    chart: {{ type: 'bar', height: 350, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [
      {{ name: 'Before (ms)', data: [{beforeElapsedData}] }},
      {{ name: 'Best After (ms)', data: [{afterElapsedData}] }}
    ],
    xaxis: {{ categories: [{chartLabels}], labels: {{ rotate: -45, style: {{ fontSize: '11px' }} }} }},
    yaxis: {{ title: {{ text: 'Elapsed Time (ms)' }} }},
    colors: ['#ef4444', '#10b981'],
    plotOptions: {{ bar: {{ borderRadius: 4, columnWidth: '60%' }} }},
    dataLabels: {{ enabled: true, formatter: function(v) {{ return v + 'ms'; }}, style: {{ fontSize: '11px' }} }},
    theme: {{ mode: 'light' }}
  }}).render();");
            sb.AppendLine("  </script>");
            sb.AppendLine("</section>");

            // Chart 3: Before vs Best After — CPU Time
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("  <h2>Before vs Best After — CPU Time</h2>");
            sb.AppendLine("  <div id=\"chartCpu\" class=\"chart-box\"></div>");
            sb.AppendLine("  <script>");
            sb.AppendLine($@"  new ApexCharts(document.querySelector('#chartCpu'), {{
    chart: {{ type: 'bar', height: 350, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [
      {{ name: 'Before CPU (ms)', data: [{beforeCpuData}] }},
      {{ name: 'Best After CPU (ms)', data: [{afterCpuData}] }}
    ],
    xaxis: {{ categories: [{chartLabels}], labels: {{ rotate: -45, style: {{ fontSize: '11px' }} }} }},
    yaxis: {{ title: {{ text: 'CPU Time (ms)' }} }},
    colors: ['#8b5cf6', '#06b6d4'],
    plotOptions: {{ bar: {{ borderRadius: 4, columnWidth: '60%' }} }},
    dataLabels: {{ enabled: true, formatter: function(v) {{ return v + 'ms'; }}, style: {{ fontSize: '11px' }} }},
    theme: {{ mode: 'light' }}
  }}).render();");
            sb.AppendLine("  </script>");
            sb.AppendLine("</section>");
        }

        sb.AppendLine("</main>");

        // Footer
        sb.AppendLine("<footer>");
        sb.AppendLine("  <p>Tedd.AutoSqlOptimizer &mdash; Automated SQL Performance Testing</p>");
        sb.AppendLine("</footer>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        var reportPath = Path.Combine(runFolder, "summary.html");
        File.WriteAllText(reportPath, sb.ToString());
    }

    // ───────────────────────────────────────────────────
    //  HELPER METHODS
    // ───────────────────────────────────────────────────

    private static string He(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    private static string Ejs(string s) => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F0}s";
        if (ts.TotalMinutes < 60) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
    }

    private static string? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void TryRenderSqlFile(StringBuilder sb, string folder, string fileName, string title)
    {
        var path = Path.Combine(folder, fileName);
        var content = TryReadFile(path);
        if (string.IsNullOrWhiteSpace(content)) return;

        sb.AppendLine("  <details class=\"sql-expand\">");
        sb.AppendLine($"    <summary>{He(title)}</summary>");
        sb.AppendLine($"    <pre><code class=\"language-sql\">{He(content)}</code></pre>");
        sb.AppendLine("  </details>");
    }

    private static void TryRenderSqlFileInline(StringBuilder sb, string folder, string fileName, string title)
    {
        var path = Path.Combine(folder, fileName);
        var content = TryReadFile(path);
        if (string.IsNullOrWhiteSpace(content)) return;

        sb.AppendLine($"    <h4>{He(title)}</h4>");
        sb.AppendLine($"    <pre><code class=\"language-sql\">{He(content)}</code></pre>");
    }

    private static void RenderApexLineChart(StringBuilder sb, string elId, BenchmarkResult result)
    {
        var cpuData = string.Join(",", result.Timings.Select(t => t.CpuTimeMs));
        var elapsedData = string.Join(",", result.Timings.Select(t => t.ElapsedTimeMs));
        var labels = string.Join(",", Enumerable.Range(1, result.Timings.Count));

        sb.AppendLine("  <script>");
        sb.AppendLine($@"  new ApexCharts(document.querySelector('#{elId}'), {{
    chart: {{ type: 'area', height: 300, toolbar: {{ show: true }}, fontFamily: 'Inter, system-ui, sans-serif' }},
    series: [
      {{ name: 'CPU Time (ms)', data: [{cpuData}] }},
      {{ name: 'Elapsed Time (ms)', data: [{elapsedData}] }}
    ],
    xaxis: {{ categories: [{labels}], title: {{ text: 'Iteration' }} }},
    yaxis: {{ title: {{ text: 'Time (ms)' }} }},
    colors: ['#ef4444', '#6366f1'],
    stroke: {{ width: 2, curve: 'smooth' }},
    fill: {{ type: 'gradient', gradient: {{ shadeIntensity: 1, opacityFrom: 0.4, opacityTo: 0.05 }} }},
    markers: {{ size: 4 }},
    dataLabels: {{ enabled: false }},
    theme: {{ mode: 'light' }}
  }}).render();");
        sb.AppendLine("  </script>");
    }

    private static void RenderHtmlDataTable(StringBuilder sb, string label, BenchmarkResult result)
    {
        sb.AppendLine($"  <h3>{He(label)}</h3>");
        sb.AppendLine("  <table class=\"data-table\">");
        sb.AppendLine("    <thead><tr><th>Run</th><th>CPU (ms)</th><th>Elapsed (ms)</th></tr></thead>");
        sb.AppendLine("    <tbody>");
        for (int i = 0; i < result.Timings.Count; i++)
        {
            sb.AppendLine($"    <tr><td>{i + 1}</td><td>{result.Timings[i].CpuTimeMs}</td><td>{result.Timings[i].ElapsedTimeMs}</td></tr>");
        }
        sb.AppendLine($"    <tr class=\"row-summary\"><td>Avg</td><td>{result.AvgCpu:F0}</td><td>{result.AvgElapsed:F0}</td></tr>");
        sb.AppendLine($"    <tr class=\"row-summary\"><td>Median</td><td>{result.MedianCpu}</td><td>{result.MedianElapsed}</td></tr>");
        sb.AppendLine($"    <tr class=\"row-summary\"><td>Min</td><td>{result.MinCpu}</td><td>{result.MinElapsed}</td></tr>");
        sb.AppendLine($"    <tr class=\"row-summary\"><td>Max</td><td>{result.MaxCpu}</td><td>{result.MaxElapsed}</td></tr>");
        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
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

    // ───────────────────────────────────────────────────
    //  CSS STYLES
    // ───────────────────────────────────────────────────

    private static string GetSummaryPageCss() => @"
    :root {
      --bg: #0f172a;
      --bg-card: #1e293b;
      --bg-card-hover: #263548;
      --text: #e2e8f0;
      --text-muted: #94a3b8;
      --border: #334155;
      --accent: #6366f1;
      --accent-glow: rgba(99,102,241,0.3);
      --green: #10b981;
      --green-bg: rgba(16,185,129,0.12);
      --red: #ef4444;
      --red-bg: rgba(239,68,68,0.12);
      --yellow: #f59e0b;
      --yellow-bg: rgba(245,158,11,0.12);
      --blue: #3b82f6;
      --blue-bg: rgba(59,130,246,0.15);
      --radius: 12px;
      --shadow: 0 4px 24px rgba(0,0,0,0.3);
    }
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body {
      font-family: 'Inter', 'Segoe UI', system-ui, -apple-system, sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.6;
      min-height: 100vh;
    }
    header {
      background: linear-gradient(135deg, #1e1b4b 0%, #312e81 50%, #1e293b 100%);
      padding: 40px 32px 32px;
      border-bottom: 1px solid var(--border);
    }
    .header-content { max-width: 1200px; margin: 0 auto; }
    .logo {
      font-size: 13px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 3px;
      color: var(--accent);
      margin-bottom: 8px;
    }
    header h1 {
      font-size: 28px;
      font-weight: 700;
      color: #fff;
      margin-bottom: 6px;
    }
    .subtitle { color: var(--text-muted); font-size: 13px; }
    main { max-width: 1200px; margin: 0 auto; padding: 24px 32px 60px; }
    .dashboard { margin-bottom: 24px; }
    .kpi-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
      gap: 16px;
      margin-bottom: 16px;
    }
    .kpi-card {
      background: var(--bg-card);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 20px;
      text-align: center;
      transition: transform 0.15s, box-shadow 0.15s;
    }
    .kpi-card:hover { transform: translateY(-2px); box-shadow: var(--shadow); }
    .kpi-value {
      font-size: 28px;
      font-weight: 800;
      color: #fff;
      line-height: 1.2;
    }
    .kpi-unit { font-size: 14px; font-weight: 400; color: var(--text-muted); margin-left: 2px; }
    .kpi-label { font-size: 12px; color: var(--text-muted); margin-top: 4px; text-transform: uppercase; letter-spacing: 0.5px; }
    .kpi-delta { font-size: 12px; margin-top: 4px; }
    .kpi-good { border-color: var(--green); background: var(--green-bg); }
    .kpi-good .kpi-value { color: var(--green); }
    .kpi-bad { border-color: var(--red); background: var(--red-bg); }
    .kpi-bad .kpi-value { color: var(--red); }
    .kpi-running { border-color: var(--blue); background: var(--blue-bg); }
    .kpi-running .kpi-value { color: var(--blue); }
    .kpi-highlight { border-color: var(--accent); background: rgba(99,102,241,0.1); }
    .kpi-highlight .kpi-value { color: var(--accent); font-size: 16px; }
    .running-status {
      background: var(--blue-bg);
      border: 1px solid rgba(59,130,246,0.3);
      border-radius: var(--radius);
      padding: 12px 20px;
      font-size: 14px;
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .pulse, .pulse-sm {
      display: inline-block;
      width: 10px; height: 10px;
      background: var(--blue);
      border-radius: 50%;
      animation: pulse-anim 1.5s ease-in-out infinite;
    }
    .pulse-sm { width: 8px; height: 8px; }
    @keyframes pulse-anim {
      0%, 100% { opacity: 1; transform: scale(1); }
      50% { opacity: 0.5; transform: scale(1.3); }
    }
    .card {
      background: var(--bg-card);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 24px;
      margin-bottom: 24px;
      box-shadow: var(--shadow);
    }
    .card h2 {
      font-size: 18px;
      font-weight: 700;
      color: #fff;
      margin-bottom: 16px;
      padding-bottom: 8px;
      border-bottom: 1px solid var(--border);
    }
    .table-wrap { overflow-x: auto; }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
    }
    thead th {
      background: rgba(99,102,241,0.15);
      color: var(--accent);
      font-weight: 600;
      text-transform: uppercase;
      font-size: 11px;
      letter-spacing: 0.5px;
      padding: 12px 14px;
      text-align: left;
      border-bottom: 2px solid var(--border);
      white-space: nowrap;
    }
    td {
      padding: 10px 14px;
      border-bottom: 1px solid var(--border);
      vertical-align: middle;
    }
    tbody tr:hover { background: var(--bg-card-hover); }
    .improvement { color: var(--green); font-weight: 700; }
    .regression { color: var(--red); font-weight: 700; }
    .status-running { color: var(--blue); font-weight: 600; display: inline-flex; align-items: center; gap: 6px; }
    .status-done { color: var(--green); font-weight: 600; }
    .status-failed { color: var(--red); font-weight: 600; }
    .status-pending { color: var(--text-muted); }
    .link-btn {
      display: inline-block;
      padding: 4px 12px;
      background: var(--accent);
      color: #fff;
      text-decoration: none;
      border-radius: 6px;
      font-size: 12px;
      font-weight: 600;
      transition: background 0.15s;
    }
    .link-btn:hover { background: #4f46e5; }
    .chart-box { min-height: 350px; }
    footer {
      text-align: center;
      padding: 24px;
      color: var(--text-muted);
      font-size: 12px;
      border-top: 1px solid var(--border);
    }
";

    private static string GetDetailPageCss() => @"
    :root {
      --bg: #0f172a;
      --bg-card: #1e293b;
      --bg-card-hover: #263548;
      --text: #e2e8f0;
      --text-muted: #94a3b8;
      --border: #334155;
      --accent: #6366f1;
      --green: #10b981;
      --green-bg: rgba(16,185,129,0.12);
      --red: #ef4444;
      --red-bg: rgba(239,68,68,0.12);
      --yellow: #f59e0b;
      --blue: #3b82f6;
      --radius: 12px;
      --shadow: 0 4px 24px rgba(0,0,0,0.3);
    }
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body {
      font-family: 'Inter', 'Segoe UI', system-ui, -apple-system, sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.6;
      min-height: 100vh;
    }
    header {
      background: linear-gradient(135deg, #1e1b4b 0%, #312e81 50%, #1e293b 100%);
      padding: 32px;
      border-bottom: 1px solid var(--border);
    }
    .header-content { max-width: 1100px; margin: 0 auto; }
    header h1 { font-size: 24px; font-weight: 700; color: #fff; margin-bottom: 4px; }
    .subtitle { color: var(--text-muted); font-size: 13px; }
    .back-link { color: var(--accent); text-decoration: none; font-weight: 600; }
    .back-link:hover { text-decoration: underline; }
    main { max-width: 1100px; margin: 0 auto; padding: 24px 32px 60px; }
    .hero-results { margin-bottom: 24px; }
    .hero-results h2 {
      font-size: 20px; font-weight: 700; color: #fff; margin-bottom: 16px;
    }
    .kpi-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
      gap: 14px;
    }
    .kpi-card {
      background: var(--bg-card);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 18px;
      text-align: center;
      transition: transform 0.15s;
    }
    .kpi-card:hover { transform: translateY(-2px); }
    .kpi-value { font-size: 26px; font-weight: 800; color: #fff; line-height: 1.2; }
    .kpi-unit { font-size: 13px; font-weight: 400; color: var(--text-muted); }
    .kpi-label { font-size: 11px; color: var(--text-muted); margin-top: 4px; text-transform: uppercase; letter-spacing: 0.5px; }
    .kpi-delta { font-size: 12px; margin-top: 4px; }
    .kpi-good { border-color: var(--green); background: var(--green-bg); }
    .kpi-good .kpi-value, .kpi-good .kpi-delta { color: var(--green); }
    .kpi-bad { border-color: var(--red); background: var(--red-bg); }
    .kpi-bad .kpi-value, .kpi-bad .kpi-delta { color: var(--red); }
    .kpi-highlight { border-color: var(--accent); background: rgba(99,102,241,0.1); }
    .kpi-highlight .kpi-value { color: var(--accent); font-size: 16px; }
    .card {
      background: var(--bg-card);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 24px;
      margin-bottom: 24px;
      box-shadow: var(--shadow);
    }
    .card h2 {
      font-size: 18px; font-weight: 700; color: #fff; margin-bottom: 16px;
      padding-bottom: 8px; border-bottom: 1px solid var(--border);
    }
    .card h3 { font-size: 15px; font-weight: 600; color: var(--text); margin: 16px 0 8px; }
    .stats-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
      gap: 12px;
    }
    .stat {
      text-align: center;
      padding: 12px;
      background: rgba(99,102,241,0.08);
      border-radius: 8px;
    }
    .stat-num { display: block; font-size: 24px; font-weight: 800; color: var(--accent); }
    .stat-label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.5px; }
    .ai-narrative { color: var(--text); font-size: 14px; }
    .ai-narrative p { margin-bottom: 10px; }
    .progression { padding-left: 20px; }
    .progression li { margin-bottom: 8px; font-size: 13px; }
    .badge {
      display: inline-block;
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
    }
    .badge-ok { background: var(--green-bg); color: var(--green); border: 1px solid rgba(16,185,129,0.3); }
    .badge-fail { background: var(--red-bg); color: var(--red); border: 1px solid rgba(239,68,68,0.3); }
    .improvement { color: var(--green); font-weight: 700; }
    .regression { color: var(--red); font-weight: 700; }
    .text-muted { color: var(--text-muted); font-size: 13px; }
    .table-wrap { overflow-x: auto; }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
    }
    thead th {
      background: rgba(99,102,241,0.15);
      color: var(--accent);
      font-weight: 600;
      text-transform: uppercase;
      font-size: 11px;
      letter-spacing: 0.5px;
      padding: 10px 12px;
      text-align: left;
      border-bottom: 2px solid var(--border);
      white-space: nowrap;
    }
    td {
      padding: 8px 12px;
      border-bottom: 1px solid var(--border);
      vertical-align: middle;
    }
    .desc-col { max-width: 300px; overflow: hidden; text-overflow: ellipsis; }
    tbody tr:hover { background: var(--bg-card-hover); }
    .row-best { background: rgba(16,185,129,0.08) !important; }
    .row-summary td { font-weight: 700; background: rgba(99,102,241,0.06); }
    .data-table { margin-bottom: 16px; }
    .link-btn {
      display: inline-block;
      padding: 3px 10px;
      background: var(--accent);
      color: #fff;
      text-decoration: none;
      border-radius: 5px;
      font-size: 11px;
      font-weight: 600;
    }
    .link-btn:hover { background: #4f46e5; }
    .chart-box { min-height: 300px; margin-bottom: 8px; }
    .alert { padding: 16px; border-radius: var(--radius); margin-bottom: 20px; }
    .alert-error { background: var(--red-bg); border: 1px solid rgba(239,68,68,0.3); color: var(--red); }
    .alert-error pre { color: var(--text); margin-top: 8px; white-space: pre-wrap; font-size: 12px; }
    .info-box {
      background: rgba(59,130,246,0.08);
      border: 1px solid rgba(59,130,246,0.2);
      border-radius: 8px;
      padding: 16px;
      margin-bottom: 16px;
    }
    .info-box h3 { color: var(--blue); margin: 0 0 8px; }
    .info-box p { margin-bottom: 8px; font-size: 13px; }
    .info-box code { background: rgba(99,102,241,0.15); padding: 2px 6px; border-radius: 4px; font-size: 12px; }
    .sql-expand {
      margin-bottom: 12px;
      border: 1px solid var(--border);
      border-radius: 8px;
      overflow: hidden;
    }
    .sql-expand summary {
      padding: 10px 16px;
      cursor: pointer;
      font-weight: 600;
      font-size: 13px;
      background: rgba(99,102,241,0.08);
      border-bottom: 1px solid var(--border);
      user-select: none;
    }
    .sql-expand summary:hover { background: rgba(99,102,241,0.15); }
    .sql-expand[open] summary { border-bottom: 1px solid var(--border); }
    .sql-expand pre {
      margin: 0;
      padding: 16px;
      overflow-x: auto;
      font-size: 12px;
      background: #0d1117 !important;
    }
    .sql-expand pre code {
      background: transparent !important;
      font-size: 12px;
    }
    .sql-expand h4 { padding: 10px 16px 4px; font-size: 13px; color: var(--text-muted); }
";
}
