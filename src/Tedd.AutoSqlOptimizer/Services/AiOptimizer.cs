using Tedd.AutoSqlOptimizer.Models;

using Microsoft.Data.SqlClient;

using OpenAI;
using OpenAI.Chat;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tedd.AutoSqlOptimizer.Services;

public class AiOptimizer
{
    private readonly BenchmarkConfig _config;
    private readonly SqlExecutor _sqlExecutor;
    private readonly Action<string> _log;

    public AiOptimizer(BenchmarkConfig config, SqlExecutor sqlExecutor, Action<string> log)
    {
        _config = config;
        _sqlExecutor = sqlExecutor;
        _log = log;
    }

    public record AiOptimizationResult(
        string Name,
        string Description,
        string OptimizeSql,
        string RevertSql,
        BenchmarkResult? AfterResult,
        bool OptimizeSucceeded,
        bool RevertSucceeded,
        int OptimizeAttempts,
        int RevertAttempts,
        string Folder,
        string? ErrorMessage = null,
        bool DataIntegrityOk = true,
        string? DataIntegrityNotes = null
    );

    /// <summary>Holds schema discovery output: contextual markdown + identified base tables.</summary>
    private record SchemaDiscoveryResult(
        string SchemaInfo,
        List<(string Schema, string Table)> BaseTables
    );

    public async Task<List<AiOptimizationResult>> RunAiOptimizationsAsync(
        SqlConnection conn,
        OptimizationFolder optimization,
        BenchmarkResult beforeResult,
        string outputFolder,
        Action<AiOptimizationResult>? onAttemptComplete = null)
    {
        var results = new List<AiOptimizationResult>();
        var apiKey = _config.OpenAI.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log("ERROR: No OpenAI API key configured. Cannot run AI optimizations.");
            return results;
        }

        // Phase 1: Gather schema info + identify base tables
        _log("=== AI Optimization: Phase 1 — Schema Discovery (AI) ===");
        var discovery = await GatherSchemaInfoWithAiAsync(conn, optimization.BeforeSql, apiKey);
        var schemaInfo = discovery.SchemaInfo;
        var baseTables = discovery.BaseTables;

        // Filter out tables matching the skip pattern
        if (!string.IsNullOrEmpty(_config.IntegrityCheckSkipPattern))
        {
            try
            {
                var skipRegex = new Regex(_config.IntegrityCheckSkipPattern, RegexOptions.IgnoreCase);
                var filtered = baseTables.Where(t => !skipRegex.IsMatch($"{t.Schema}.{t.Table}")).ToList();
                var skipped = baseTables.Where(t => skipRegex.IsMatch($"{t.Schema}.{t.Table}")).ToList();

                foreach (var st in skipped)
                {
                    _log($"  [DataIntegrity] Skipping integrity check for [{st.Schema}].[{st.Table}] due to IntegrityCheckSkipPattern.");
                }
                baseTables = filtered;
            }
            catch (Exception ex)
            {
                _log($"  [WARNING] Invalid IntegrityCheckSkipPattern regex: {ex.Message}");
            }
        }

        _log($"Schema info gathered ({schemaInfo.Length} chars)");
        _log($"Base tables identified: {(baseTables.Count > 0 ? string.Join(", ", baseTables.Select(t => $"[{t.Schema}].[{t.Table}]")) : "(none)")}\n");

        // Compute baseline data checksums BEFORE any optimization
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> baselineChecksums = new(StringComparer.OrdinalIgnoreCase);
        if (baseTables.Count > 0)
        {
            _log("=== Computing Baseline Data Checksums ===");
            baselineChecksums = _sqlExecutor.ComputeDataChecksums(conn, baseTables);
            _log($"Baseline checksums computed for {baselineChecksums.Count} table(s).");
        }

        try
        {
            // Phase 2 & 3: Loop over AI optimization attempts
            var previousAttemptsSummary = new StringBuilder();

            for (int i = 1; i <= _config.AiOptimizationCount; i++)
            {
                _log($"\n=== AI Optimization Attempt {i}/{_config.AiOptimizationCount} ===");
                var aiFolder = Path.Combine(outputFolder, $"ai_opt_{i}");
                Directory.CreateDirectory(aiFolder);

                // Build prompt (includes SQL analysis and schema context)
                var prompt = BuildPrompt(optimization.BeforeSql, schemaInfo, beforeResult,
                    previousAttemptsSummary.ToString(), i, baseTables);

                // Save prompt for debugging
                File.WriteAllText(Path.Combine(aiFolder, "ai_prompt.txt"), prompt);
                _log($"Prompt saved to ai_opt_{i}/ai_prompt.txt");

                // Call OpenAI
                var (optimizeSql, revertSql, description) = await CallOpenAiAsync(apiKey, prompt);

                if (string.IsNullOrWhiteSpace(optimizeSql))
                {
                    _log("AI returned empty optimization SQL. Skipping.");
                    previousAttemptsSummary.AppendLine($"Attempt {i}: AI returned empty response. Skipped.");
                    continue;
                }

                File.WriteAllText(Path.Combine(aiFolder, "2_optimize.sql"), optimizeSql);
                File.WriteAllText(Path.Combine(aiFolder, "4_revert.sql"), revertSql);
                File.WriteAllText(Path.Combine(aiFolder, "description.txt"), description);
                _log($"AI suggestion: {description}");

                // Phase 3: Apply & Test with retries (includes data integrity check)
                var result = await ApplyAndTestAsync(conn, optimization, aiFolder,
                    optimizeSql, revertSql, description, beforeResult, $"AI Opt {i}",
                    baselineChecksums, baseTables);
                results.Add(result);
                onAttemptComplete?.Invoke(result);

                // Build summary for next attempt
                var summary = new StringBuilder();
                summary.AppendLine($"### Pass {i}: {description}");
                summary.AppendLine("  **SQL Tried:**");
                summary.AppendLine("  ```sql");
                summary.AppendLine(result.OptimizeSql);
                summary.AppendLine("  ```");

                if (result.OptimizeSucceeded)
                {
                    summary.AppendLine("  **Status:** Optimization applied successfully.");
                    if (result.AfterResult != null)
                    {
                        var cpuImprovement = beforeResult.AvgCpu > 0
                            ? (1 - result.AfterResult.AvgCpu / beforeResult.AvgCpu) * 100 : 0;
                        var elapsedImprovement = beforeResult.AvgElapsed > 0
                            ? (1 - result.AfterResult.AvgElapsed / beforeResult.AvgElapsed) * 100 : 0;

                        summary.AppendLine($"  **Results:** CPU {cpuImprovement:+0.1f;-0.1f}% | Elapsed {elapsedImprovement:+0.1f;-0.1f}%");
                        summary.AppendLine($"  **Timing:** AvgCPU={result.AfterResult.AvgCpu:F0}ms (vs {beforeResult.AvgCpu:F0}ms), AvgElapsed={result.AfterResult.AvgElapsed:F0}ms (vs {beforeResult.AvgElapsed:F0}ms)");

                        if (cpuImprovement > 5 || elapsedImprovement > 5)
                            summary.AppendLine("  **Outcome:** GOOD (improvement)");
                        else if (cpuImprovement < -5 || elapsedImprovement < -5)
                            summary.AppendLine("  **Outcome:** BAD (regression)");
                        else
                            summary.AppendLine("  **Outcome:** NO SIGNIFICANT CHANGE");
                    }
                }
                else
                {
                    summary.AppendLine("  **Status:** FAILED to apply.");
                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        summary.AppendLine($"  **Error:** {result.ErrorMessage}");
                    }
                }
                summary.AppendLine();
                previousAttemptsSummary.Append(summary);

                if (!result.RevertSucceeded)
                {
                    _log("CRITICAL: Revert failed after all retries. STOPPING AI optimizations to prevent further damage.");
                    break;
                }
            }

            // Phase 4: Combined Optimization
            // Only if we had multiple attempts and at least one succeeded optimize, and everything is currently reverted
            if (results.Count > 1 && results.Any(r => r.OptimizeSucceeded) && results.All(r => r.RevertSucceeded))
            {
                _log("\n=== AI Optimization: Phase 4 — Combined/Ultimate Optimization ===");
                var combinedFolder = Path.Combine(outputFolder, "ai_opt_combined");
                Directory.CreateDirectory(combinedFolder);

                // Build prompt for combined optimization
                var combinedPrompt = BuildCombinedPrompt(optimization.BeforeSql, schemaInfo, beforeResult, previousAttemptsSummary.ToString());

                // Save prompt for debugging
                File.WriteAllText(Path.Combine(combinedFolder, "ai_prompt.txt"), combinedPrompt);
                _log("Combined prompt saved to ai_opt_combined/ai_prompt.txt");

                // Call OpenAI for the ultimate combined optimization
                var (combinedOptimizeSql, combinedRevertSql, combinedDescription) = await CallOpenAiAsync(apiKey, combinedPrompt);

                if (!string.IsNullOrWhiteSpace(combinedOptimizeSql))
                {
                    File.WriteAllText(Path.Combine(combinedFolder, "2_optimize.sql"), combinedOptimizeSql);
                    File.WriteAllText(Path.Combine(combinedFolder, "4_revert.sql"), combinedRevertSql);
                    File.WriteAllText(Path.Combine(combinedFolder, "description.txt"), combinedDescription);
                    _log($"AI Combined suggestion: {combinedDescription}");

                    // Apply & Test the combined result (includes data integrity check)
                    var combinedResult = await ApplyAndTestAsync(conn, optimization, combinedFolder,
                        combinedOptimizeSql, combinedRevertSql, combinedDescription, beforeResult, "AI Combined",
                        baselineChecksums, baseTables);

                    results.Add(combinedResult);
                    onAttemptComplete?.Invoke(combinedResult);
                }
                else
                {
                    _log("AI returned empty combined optimization SQL. Skipping phase 4.");
                }
            }
        }
        catch (Exception ex)
        {
            _log($"FATAL ERROR during AI optimization loop: {ex.Message}");
            // Return what we have so far
        }

        return results;
    }

    private async Task<AiOptimizationResult> ApplyAndTestAsync(
        SqlConnection conn,
        OptimizationFolder optimization,
        string aiFolder,
        string optimizeSql,
        string revertSql,
        string description,
        BenchmarkResult beforeResult,
        string name,
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> baselineChecksums,
        List<(string Schema, string Table)> baseTables)
    {
        bool optimizeSucceeded = false;
        int optimizeAttempts = 0;
        BenchmarkResult? afterResult = null;
        bool revertSucceeded = false;
        int revertAttempts = 0;
        string? lastErrorMessage = null;
        bool dataIntegrityOk = true;
        var dataIntegrityNotes = new StringBuilder();

        var currentOptimizeSql = optimizeSql;
        var currentRevertSql = revertSql;

        // Try to apply optimization (with retries)
        for (int retry = 1; retry <= _config.AiMaxRetries; retry++)
        {
            optimizeAttempts = retry;
            _log($"  Applying optimization (attempt {retry}/{_config.AiMaxRetries})...");
            try
            {
                _sqlExecutor.ExecuteNonQuery(conn, currentOptimizeSql);
                optimizeSucceeded = true;
                lastErrorMessage = null;
                _log("  Optimization applied successfully.");
                break;
            }
            catch (Exception ex)
            {
                lastErrorMessage = ex.Message;
                _log($"  ERROR applying optimization: {ex.Message}");
                File.WriteAllText(Path.Combine(aiFolder, $"2_optimize_attempt_{retry}.sql"), currentOptimizeSql);
                File.WriteAllText(Path.Combine(aiFolder, $"2_optimize_error_{retry}.txt"), ex.ToString());

                if (retry < _config.AiMaxRetries)
                {
                    _log("  Asking AI to fix the optimization SQL...");
                    var fixPrompt = BuildFixPrompt(currentOptimizeSql, ex.Message, "optimize");
                    var apiKey = _config.OpenAI.ApiKey;
                    if (string.IsNullOrWhiteSpace(apiKey))
                        apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
                    var (fixedSql, fixedRevert, _) = await CallOpenAiAsync(apiKey, fixPrompt);
                    if (!string.IsNullOrWhiteSpace(fixedSql))
                    {
                        currentOptimizeSql = fixedSql;
                        if (!string.IsNullOrWhiteSpace(fixedRevert))
                            currentRevertSql = fixedRevert;
                    }
                }
            }
        }

        // If optimization succeeded, benchmark it
        if (optimizeSucceeded)
        {
            // Verify data integrity AFTER apply (before benchmarking)
            if (baseTables.Count > 0 && baselineChecksums.Count > 0)
            {
                _log("  [DataIntegrity] Verifying data integrity after optimization...");
                var afterOptChecksums = _sqlExecutor.ComputeDataChecksums(conn, baseTables);
                VerifyDataIntegrity("after optimization", baselineChecksums, afterOptChecksums,
                    dataIntegrityNotes, ref dataIntegrityOk);
            }

            _log("  Running benchmark after optimization...");
            
            // Update statistics before benchmarking optimization
            _sqlExecutor.UpdateStatistics(conn);
            
            afterResult = new BenchmarkResult { Label = name };

            for (int run = 1; run <= _config.BenchmarkIterations; run++)
            {
                _sqlExecutor.ClearCache(conn);
                var timing = _sqlExecutor.ExecuteWithTiming(conn, optimization.AfterSql);
                afterResult.Timings.Add(timing);
                _log($"    Run {run}/{_config.BenchmarkIterations}: {timing}");
            }

            _log($"  After results: AvgCPU={afterResult.AvgCpu:F0}ms, AvgElapsed={afterResult.AvgElapsed:F0}ms");

            // Now revert (with retries)
            for (int retry = 1; retry <= _config.AiMaxRetries; retry++)
            {
                revertAttempts = retry;
                _log($"  Reverting optimization (attempt {retry}/{_config.AiMaxRetries})...");
                try
                {
                    _sqlExecutor.ExecuteNonQuery(conn, currentRevertSql);
                    revertSucceeded = true;
                    _log("  Revert applied successfully.");
                    break;
                }
                catch (Exception ex)
                {
                    _log($"  ERROR reverting: {ex.Message}");
                    File.WriteAllText(Path.Combine(aiFolder, $"4_revert_attempt_{retry}.sql"), currentRevertSql);
                    File.WriteAllText(Path.Combine(aiFolder, $"4_revert_error_{retry}.txt"), ex.ToString());

                    if (retry < _config.AiMaxRetries)
                    {
                        _log("  Asking AI to fix the revert SQL...");
                        var fixPrompt = BuildFixPrompt(currentRevertSql, ex.Message, "revert",
                            currentOptimizeSql);
                        var apiKey = _config.OpenAI.ApiKey;
                        if (string.IsNullOrWhiteSpace(apiKey))
                            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
                        var (_, fixedRevert, _) = await CallOpenAiAsync(apiKey, fixPrompt);
                        if (!string.IsNullOrWhiteSpace(fixedRevert))
                            currentRevertSql = fixedRevert;
                    }
                }
            }

            // Verify revert by running before SQL and comparing timing
            if (revertSucceeded)
            {
                _log("  Verifying revert by running before SQL...");
                
                // Update statistics after reverting, before verification
                _sqlExecutor.UpdateStatistics(conn);
                
                try
                {
                    _sqlExecutor.ClearCache(conn);
                    var verifyTiming = _sqlExecutor.ExecuteWithTiming(conn, optimization.BeforeSql);
                    _log($"  Verification timing: {verifyTiming}");

                    // Allow 50% margin — if significantly faster than baseline, revert may not have worked
                    if (beforeResult.AvgElapsed > 0 && verifyTiming.ElapsedTimeMs < beforeResult.AvgElapsed * 0.5)
                    {
                        _log("  WARNING: Post-revert timing is significantly faster than baseline. Revert may not have fully undone the optimization.");
                    }
                }
                catch (Exception ex)
                {
                    _log($"  ERROR verifying revert: {ex.Message}");
                    // We don't fail the whole result here since revertSucceeded is already true
                }

                // Verify data integrity AFTER revert (checksums must match baseline)
                if (baseTables.Count > 0 && baselineChecksums.Count > 0)
                {
                    _log("  [DataIntegrity] Verifying data integrity after revert...");
                    var afterRevertChecksums = _sqlExecutor.ComputeDataChecksums(conn, baseTables);
                    VerifyDataIntegrity("after revert", baselineChecksums, afterRevertChecksums,
                        dataIntegrityNotes, ref dataIntegrityOk);

                    if (dataIntegrityOk)
                        _log("  [DataIntegrity] ✓ All table checksums match baseline. Data integrity confirmed.");
                    else
                        _log($"  [DataIntegrity] ✗ DATA INTEGRITY ISSUES DETECTED:\n{dataIntegrityNotes}");

                    // Save integrity report
                    File.WriteAllText(Path.Combine(aiFolder, "data_integrity_report.txt"),
                        $"Data Integrity Check: {(dataIntegrityOk ? "PASSED" : "FAILED")}\n\n{dataIntegrityNotes}");
                }
            }

            // Save final SQL files
            File.WriteAllText(Path.Combine(aiFolder, "2_optimize.sql"), currentOptimizeSql);
            File.WriteAllText(Path.Combine(aiFolder, "4_revert.sql"), currentRevertSql);
        }

        return new AiOptimizationResult(
            Name: name,
            Description: description,
            OptimizeSql: currentOptimizeSql,
            RevertSql: currentRevertSql,
            AfterResult: afterResult,
            OptimizeSucceeded: optimizeSucceeded,
            RevertSucceeded: revertSucceeded || !optimizeSucceeded, // revert not needed if optimize failed
            OptimizeAttempts: optimizeAttempts,
            RevertAttempts: revertAttempts,
            Folder: aiFolder,
            ErrorMessage: lastErrorMessage,
            DataIntegrityOk: dataIntegrityOk,
            DataIntegrityNotes: dataIntegrityNotes.Length > 0 ? dataIntegrityNotes.ToString() : null
        );
    }

    /// <summary>
    /// Compares two checksum snapshots and appends findings to <paramref name="notes"/>.
    /// Sets <paramref name="overallOk"/> to false if any mismatch is found.
    /// </summary>
    private void VerifyDataIntegrity(
        string phase,
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> baseline,
        Dictionary<string, (long RowCount, long? Checksum, string Summary)> current,
        StringBuilder notes,
        ref bool overallOk)
    {
        notes.AppendLine($"--- Integrity check {phase} ---");
        bool phaseOk = true;

        foreach (var (key, baseVal) in baseline)
        {
            if (!current.TryGetValue(key, out var curVal))
            {
                var msg = $"  TABLE {key}: NOT FOUND in current snapshot!";
                notes.AppendLine(msg);
                _log($"  [DataIntegrity] ✗ {msg}");
                phaseOk = false;
                overallOk = false;
                continue;
            }

            bool rowCountMatch = curVal.RowCount == baseVal.RowCount;
            bool checksumMatch = baseVal.Checksum == null || curVal.Checksum == null
                ? true  // can't compare NULLs definitively
                : curVal.Checksum == baseVal.Checksum;

            if (!rowCountMatch || !checksumMatch)
            {
                var msg = $"  TABLE {key}: MISMATCH! Baseline=[{baseVal.Summary}] Current=[{curVal.Summary}]";
                notes.AppendLine(msg);
                _log($"  [DataIntegrity] ✗ {msg}");
                phaseOk = false;
                overallOk = false;
            }
            else
            {
                notes.AppendLine($"  TABLE {key}: OK ({baseVal.Summary})");
                _log($"  [DataIntegrity] ✓ {key}: OK");
            }
        }

        notes.AppendLine(phaseOk ? "  => PASSED" : "  => FAILED");
        notes.AppendLine();
    }

    private async Task<SchemaDiscoveryResult> GatherSchemaInfoWithAiAsync(SqlConnection conn, string beforeSql, string apiKey)
    {
        var identifiedBaseTables = new List<(string Schema, string Table)>();
        var client = new ChatClient(_config.OpenAI.Model, apiKey);

        var toolGetSchemas = ChatTool.CreateFunctionTool(
            functionName: "get_all_schemas",
            functionDescription: "Lists all non-system schemas in the current database. Use this to understand what schemas exist."
        );

        var toolGetObjectDefinition = ChatTool.CreateFunctionTool(
            functionName: "get_object_definition",
            functionDescription: "Gets the definition (SQL source code) of a view, stored procedure, or function. Use this to drill down into referenced objects.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "schema_name": { "type": "string", "description": "The schema name" },
                "object_name": { "type": "string", "description": "The object name (view or procedure)" }
              },
              "required": ["schema_name", "object_name"]
            }
            """)
        );

        var toolGetTableColumns = ChatTool.CreateFunctionTool(
            functionName: "get_table_columns",
            functionDescription: "Gets the columns, data types, and nullability of a table.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "schema_name": { "type": "string", "description": "The schema name" },
                "table_name": { "type": "string", "description": "The table name" }
              },
              "required": ["schema_name", "table_name"]
            }
            """)
        );

        var toolGetTableIndexes = ChatTool.CreateFunctionTool(
            functionName: "get_table_indexes",
            functionDescription: "Gets the existing indexes for a table.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "schema_name": { "type": "string", "description": "The schema name" },
                "table_name": { "type": "string", "description": "The table name" }
              },
              "required": ["schema_name", "table_name"]
            }
            """)
        );

        var toolGetTableCompression = ChatTool.CreateFunctionTool(
            functionName: "get_table_compression",
            functionDescription: "Gets the current data compression setting (NONE, ROW, PAGE, COLUMNSTORE, or COLUMNSTORE_ARCHIVE) for a table and all of its indexes. Use this to avoid suggesting a compression that is already applied and to understand what compression strategy can be tried.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "schema_name": { "type": "string", "description": "The schema name" },
                "table_name": { "type": "string", "description": "The table name" }
              },
              "required": ["schema_name", "table_name"]
            }
            """)
        );

        // Tool for AI to explicitly declare which base tables (not views) it identified.
        // This is used to compute data checksums for integrity verification.
        var toolRegisterBaseTables = ChatTool.CreateFunctionTool(
            functionName: "register_base_tables",
            functionDescription: "Register the list of all base (physical) tables involved in the query. Call this once you have finished exploring the schema and know all the underlying tables. Do NOT include views — only actual tables.",
            functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "tables": {
                  "type": "array",
                  "description": "List of base tables",
                  "items": {
                    "type": "object",
                    "properties": {
                      "schema_name": { "type": "string" },
                      "table_name": { "type": "string" }
                    },
                    "required": ["schema_name", "table_name"]
                  }
                }
              },
              "required": ["tables"]
            }
            """)
        );

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You are an expert SQL Server database schema extraction agent. " +
                "You are given a SQL query. Your goal is to: " +
                "(a) understand what the SQL does and document it, " +
                "(b) gather all necessary database schema context (tables, columns, indexes, referenced views and stored procedures), " +
                "so that a later AI agent can optimize the query.\n\n" +
                "Instructions:\n" +
                "1. First, briefly summarize what the SQL query does (its purpose and the data it reads/writes).\n" +
                "2. If the query references views or stored procedures, use `get_object_definition` to read their definitions.\n" +
                "3. Keep drilling down recursively (e.g., if a proc calls a view, and that view queries tables, get the view definition, then get the tables).\n" +
                "4. Once you know which base (physical) tables are involved, use `get_table_columns` and `get_table_indexes` for them.\n" +
                "5. Identify any schemas via `get_all_schemas` if you are unsure if a prefix is a schema or database.\n" +
                "6. Important: In two-part names like `[Prefix].[ObjectName]`, assume `Prefix` is the SCHEMA name, not the database name (e.g., `[PSA_MAT].[MyTable]` means schema `PSA_MAT`).\n" +
                "7. IMPORTANT: Once you have identified all base tables (not views), call `register_base_tables` with the complete list. This is used for data integrity checking.\n" +
                "8. For each base table, also call `get_table_compression` to discover the current compression state of the table and its indexes. Include this information in the summary.\n" +
                "9. Synthesize all findings into a comprehensive markdown summary covering: the SQL's purpose, all relevant tables (columns, indexes, compression state), and all object definitions (views, procs).\n" +
                "10. Your final response to the user should ONLY be the final markdown summary of the schema context (not the base table list — that was already registered).")
        };

        messages.Add(new UserChatMessage($"Here is the SQL query to optimize:\n\n```sql\n{beforeSql}\n```\n\nPlease use your tools to explore the schema and then output the full schema context."));

        var options = new ChatCompletionOptions
        {
            Temperature = 0.2f
        };
        options.Tools.Add(toolGetSchemas);
        options.Tools.Add(toolGetObjectDefinition);
        options.Tools.Add(toolGetTableColumns);
        options.Tools.Add(toolGetTableIndexes);
        options.Tools.Add(toolGetTableCompression);
        options.Tools.Add(toolRegisterBaseTables);

        for (int i = 0; i < 20; i++)
        {
            _log($"  [Schema Discovery] AI processing (turn {i + 1})...");
            ChatCompletion completion;
            try
            {
                var completionResult = await client.CompleteChatAsync(messages, options);
                completion = completionResult.Value;
            }
            catch (Exception ex)
            {
                _log($"  [Schema Discovery] Error communicating with OpenAI: {ex.Message}");
                return new SchemaDiscoveryResult("Error during AI schema discovery: " + ex.Message, identifiedBaseTables);
            }

            if (completion.FinishReason == ChatFinishReason.Stop || completion.FinishReason == ChatFinishReason.Length)
            {
                var finalResponse = completion.Content[0].Text;
                _log($"  [Schema Discovery] Discovery complete.");
                return new SchemaDiscoveryResult(finalResponse, identifiedBaseTables);
            }

            // Append assistant response
            messages.Add(new AssistantChatMessage(completion));

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                foreach (var toolCall in completion.ToolCalls)
                {
                    _log($"  [Schema Discovery] Invoking tool: {toolCall.FunctionName}");
                    string toolResult = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(toolCall.FunctionArguments);
                        var args = doc.RootElement;

                        if (toolCall.FunctionName == "get_all_schemas")
                        {
                            var schemas = _sqlExecutor.ExecuteQuery(conn, "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')");
                            var schemaNames = schemas.Select(r => $"[{r.Values.First()}]");
                            toolResult = string.Join(", ", schemaNames);
                        }
                        else if (toolCall.FunctionName == "get_object_definition")
                        {
                            var schema = args.GetProperty("schema_name").GetString();
                            var obj = args.GetProperty("object_name").GetString();
                            var defs = _sqlExecutor.ExecuteQuery(conn, $"SELECT OBJECT_DEFINITION(OBJECT_ID('[{schema}].[{obj}]')) AS Definition");
                            if (defs.Count > 0 && defs[0].ContainsKey("Definition") && defs[0]["Definition"] != "NULL")
                                toolResult = defs[0]["Definition"];
                            else
                                toolResult = $"Object [{schema}].[{obj}] not found or definition unavailable.";
                        }
                        else if (toolCall.FunctionName == "get_table_columns")
                        {
                            var schema = args.GetProperty("schema_name").GetString();
                            var table = args.GetProperty("table_name").GetString();
                            var columns = _sqlExecutor.ExecuteQuery(conn, $@"
                                SELECT c.COLUMN_NAME, c.DATA_TYPE,
                                       c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION,
                                       c.IS_NULLABLE
                                FROM INFORMATION_SCHEMA.COLUMNS c
                                WHERE c.TABLE_SCHEMA = '{schema}' AND c.TABLE_NAME = '{table}'
                                ORDER BY c.ORDINAL_POSITION");
                            if (columns.Count == 0)
                            {
                                toolResult = $"Table [{schema}].[{table}] not found or no columns.";
                            }
                            else
                            {
                                var sb = new StringBuilder();
                                foreach (var row in columns)
                                {
                                    sb.AppendLine($"{row["COLUMN_NAME"]} ({row["DATA_TYPE"]}, maxlen={row["CHARACTER_MAXIMUM_LENGTH"]}, " +
                                                  $"prec={row["NUMERIC_PRECISION"]}, null={row["IS_NULLABLE"]})");
                                }
                                toolResult = sb.ToString();
                            }
                        }
                        else if (toolCall.FunctionName == "get_table_indexes")
                        {
                            var schema = args.GetProperty("schema_name").GetString();
                            var table = args.GetProperty("table_name").GetString();
                            try
                            {
                                var indexes = _sqlExecutor.ExecuteQuery(conn, $"EXEC sp_helpindex '[{schema}].[{table}]'");
                                if (indexes.Count == 0)
                                {
                                    toolResult = "No indexes found.";
                                }
                                else
                                {
                                    var sb = new StringBuilder();
                                    foreach (var row in indexes)
                                    {
                                        var desc = row.ContainsKey("index_description") ? row["index_description"] : "";
                                        var keys = row.ContainsKey("index_keys") ? row["index_keys"] : "";
                                        sb.AppendLine($"{row["index_name"]} ({desc}): {keys}");
                                    }
                                    toolResult = sb.ToString();
                                }
                            }
                            catch (Exception)
                            {
                                toolResult = $"Table [{schema}].[{table}] not found or no indexes.";
                            }
                        }
                        else if (toolCall.FunctionName == "get_table_compression")
                        {
                            var schema = args.GetProperty("schema_name").GetString();
                            var table = args.GetProperty("table_name").GetString();
                            try
                            {
                                // Query current compression for the heap/clustered index AND all non-clustered indexes
                                var compressionSql = $@"
SELECT
    i.name        AS index_name,
    i.type_desc   AS index_type,
    p.data_compression_desc AS compression
FROM sys.partitions p
JOIN sys.indexes i
    ON i.object_id = p.object_id AND i.index_id = p.index_id
JOIN sys.tables t
    ON t.object_id = i.object_id
JOIN sys.schemas s
    ON s.schema_id = t.schema_id
WHERE s.name = '{schema}' AND t.name = '{table}'
  AND p.partition_number = 1
ORDER BY i.index_id";
                                var comprRows = _sqlExecutor.ExecuteQuery(conn, compressionSql);
                                if (comprRows.Count == 0)
                                {
                                    toolResult = $"Table [{schema}].[{table}] not found or no partition data.";
                                }
                                else
                                {
                                    var sb = new StringBuilder();
                                    foreach (var row in comprRows)
                                    {
                                        var idxName = row.ContainsKey("index_name") ? row["index_name"] : "(heap)";
                                        var idxType = row.ContainsKey("index_type") ? row["index_type"] : "";
                                        var compr   = row.ContainsKey("compression") ? row["compression"] : "NONE";
                                        sb.AppendLine($"{idxName} ({idxType}): {compr}");
                                    }
                                    toolResult = sb.ToString();
                                }
                            }
                            catch (Exception ex)
                            {
                                toolResult = $"Error querying compression for [{schema}].[{table}]: {ex.Message}";
                            }
                        }
                        else if (toolCall.FunctionName == "register_base_tables")
                        {
                            // AI is registering which physical tables are involved
                            identifiedBaseTables.Clear();
                            var tablesArr = args.GetProperty("tables");
                            foreach (var tableElem in tablesArr.EnumerateArray())
                            {
                                var s = tableElem.GetProperty("schema_name").GetString() ?? "";
                                var t = tableElem.GetProperty("table_name").GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(t))
                                    identifiedBaseTables.Add((s, t));
                            }
                            _log($"  [Schema Discovery] AI registered {identifiedBaseTables.Count} base table(s): " +
                                 string.Join(", ", identifiedBaseTables.Select(tb => $"[{tb.Schema}].[{tb.Table}]")));
                            toolResult = $"Registered {identifiedBaseTables.Count} base table(s) successfully.";
                        }
                        else
                        {
                            toolResult = "Unknown tool call.";
                        }
                    }
                    catch (Exception ex)
                    {
                        toolResult = $"Error executing tool: {ex.Message}";
                    }

                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                }
            }
            else
            {
                // Unhandled finish reason
                _log($"  [Schema Discovery] Stopping gracefully (FinishReason: {completion.FinishReason})");
                var content = completion.Content.Count > 0 ? completion.Content[0].Text : "No content";
                return new SchemaDiscoveryResult(content, identifiedBaseTables);
            }
        }

        _log("  [Schema Discovery] Max turns reached.");
        return new SchemaDiscoveryResult("Max discovery turns reached. Final schema context may be incomplete.", identifiedBaseTables);
    }

   private string BuildPrompt(
    string beforeSql,
    string schemaInfo,
    BenchmarkResult beforeResult,
    string previousAttempts,
    int attemptNumber,
    List<(string Schema, string Table)> baseTables)
{
    var sb = new StringBuilder();
    sb.AppendLine("You are a MSSQL 2025 performance optimization expert.");
    sb.AppendLine();
    sb.AppendLine("## Task");
    sb.AppendLine("Analyze the following SQL query and suggest ONE optimization to improve its performance.");
    sb.AppendLine("Do NOT change table data or business semantics.");
    sb.AppendLine("Any optimization that changes row counts, result meaning, or persisted business data will be treated as a data integrity failure.");
    sb.AppendLine("Focus on SERVER-SIDE optimizations only. We are measuring execution performance, not client/network transfer time.");
    sb.AppendLine();
    sb.AppendLine("You may optimize by changing physical design, metadata, plan-shaping behavior, or encapsulating database objects such as views and stored procedures, provided semantics remain unchanged.");
    sb.AppendLine();
    sb.AppendLine("## SQL Query Being Tested");
    sb.AppendLine("```sql");
    sb.AppendLine(beforeSql);
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("## Current Performance (baseline, 10 runs)");
    sb.AppendLine($"- Average CPU time: {beforeResult.AvgCpu:F0} ms");
    sb.AppendLine($"- Average Elapsed time: {beforeResult.AvgElapsed:F0} ms");
    sb.AppendLine($"- Median CPU time: {beforeResult.MedianCpu} ms");
    sb.AppendLine($"- Median Elapsed time: {beforeResult.MedianElapsed} ms");
    sb.AppendLine();
    sb.AppendLine("## Database Schema Information");
    sb.AppendLine(schemaInfo);
    sb.AppendLine();
    sb.AppendLine("## Additional Domain Knowledge");
    sb.AppendLine("- Important: Most relationships in this database have `PartyId` as part of their composite foreign key. Consider this when designing indexes, join support structures, computed columns, constraints, and partition-aligned access paths.");
    sb.AppendLine();

    if (!string.IsNullOrWhiteSpace(previousAttempts))
    {
        sb.AppendLine("## Previous Optimization Attempts & Results");
        sb.AppendLine("Analyze the previous attempts carefully.");
        sb.AppendLine("Learn from which attempts were GOOD (improved performance), BAD (regressed), FAILED to apply, or created revert/integrity risks.");
        sb.AppendLine("Do NOT repeat materially the same approach if it already failed or regressed.");
        sb.AppendLine();
        sb.AppendLine(previousAttempts);
        sb.AppendLine();
    }

    sb.AppendLine("## Allowed Optimization Categories");
    sb.AppendLine($"This is attempt {attemptNumber} of {_config.AiOptimizationCount}. Try a materially different approach when appropriate.");
    sb.AppendLine();
    sb.AppendLine("You may suggest exactly ONE primary optimization from any of the following categories:");
    sb.AppendLine();
    sb.AppendLine("1. **Access path / indexing optimizations**");
    sb.AppendLine("   - Nonclustered indexes");
    sb.AppendLine("   - Covering indexes");
    sb.AppendLine("   - Indexes with included columns");
    sb.AppendLine("   - Filtered indexes");
    sb.AppendLine("   - Key order improvements for joins, predicates, grouping, and ordering");
    sb.AppendLine("   - Join-supporting indexes for composite keys such as `PartyId` + related columns");
    sb.AppendLine();
    sb.AppendLine("2. **Columnstore strategy**");
    sb.AppendLine("   - Clustered columnstore index");
    sb.AppendLine("   - Nonclustered columnstore index");
    sb.AppendLine("   - Hybrid rowstore + columnstore strategy");
    sb.AppendLine("   - Batch-mode-enabling structures for analytical or aggregation-heavy workloads");
    sb.AppendLine();
    sb.AppendLine("3. **Materialization / precomputation**");
    sb.AppendLine("   - Persisted computed columns");
    sb.AppendLine("   - Indexed computed columns");
    sb.AppendLine("   - Indexed views / schema-bound materialized aggregates or joins");
    sb.AppendLine("   - Expression-supporting structures for non-sargable predicates");
    sb.AppendLine();
    sb.AppendLine("4. **Physical storage and layout**");
    sb.AppendLine("   - Table compression (ROW, PAGE)");
    sb.AppendLine("   - Index compression");
    sb.AppendLine("   - Heap vs clustered storage improvements");
    sb.AppendLine("   - Partitioning");
    sb.AppendLine("   - Partition-aligned indexes");
    sb.AppendLine("   - Fill factor / page density related physical tuning");
    sb.AppendLine("   - Filegroup or storage-layout improvements if clearly justified");
    sb.AppendLine();
    sb.AppendLine("5. **Metadata quality / optimizer reasoning improvements**");
    sb.AppendLine("   - Create or refresh statistics");
    sb.AppendLine("   - Filtered statistics");
    sb.AppendLine("   - Trusted foreign keys");
    sb.AppendLine("   - Trusted check constraints");
    sb.AppendLine("   - Uniqueness / nullability corrections that improve optimization");
    sb.AppendLine("   - Data type alignment");
    sb.AppendLine("   - Collation alignment");
    sb.AppendLine("   - Narrowing overly wide key columns when achievable without semantic change");
    sb.AppendLine();
    sb.AppendLine("6. **Plan stability / plan-shaping optimizations**");
    sb.AppendLine("   - Query Store plan forcing");
    sb.AppendLine("   - Plan guides");
    sb.AppendLine("   - Query hints or USE HINT strategies");
    sb.AppendLine("   - MAXDOP tuning");
    sb.AppendLine("   - RECOMPILE where appropriate");
    sb.AppendLine("   - OPTIMIZE FOR strategies");
    sb.AppendLine("   - Memory grant related hints");
    sb.AppendLine("   - Parameter sniffing mitigation");
    sb.AppendLine("   - Parameter-sensitive plan mitigation");
    sb.AppendLine();
    sb.AppendLine("7. **Encapsulating object optimization**");
    sb.AppendLine("   - Rewrite stored procedures or views");
    sb.AppendLine("   - Improve temp table usage inside procedures");
    sb.AppendLine("   - Replace poor intermediate structures inside procedures/views");
    sb.AppendLine("   - Materialize expensive subexpressions inside procedures/views");
    sb.AppendLine("   - Remove implicit conversions or expression patterns that block seeks");
    sb.AppendLine();
    sb.AppendLine("8. **Maintenance / remediation optimizations**");
    sb.AppendLine("   - Rebuild or reorganize relevant indexes when justified");
    sb.AppendLine("   - Rebuild objects to apply intended compression/layout changes");
    sb.AppendLine("   - Correct fragmentation or stale metadata states that materially affect the benchmark");
    sb.AppendLine();
    sb.AppendLine("9. **Concurrency / elapsed-time focused improvements**");
    sb.AppendLine("   - Designs that reduce blocking or hot-page contention");
    sb.AppendLine("   - Structures that reduce lock escalation risk");
    sb.AppendLine("   - Physical designs that reduce I/O and latch pressure");
    sb.AppendLine();
    sb.AppendLine("## Important Constraints");
    sb.AppendLine("- Important: In two-part names like `[Prefix].[ObjectName]`, assume `Prefix` is the SCHEMA name, not the database name, unless a three-part name explicitly proves otherwise.");
    sb.AppendLine("- Do NOT modify application/business data.");
    sb.AppendLine("- Do NOT change query result semantics.");
    sb.AppendLine("- Do NOT remove rows, update values, backfill columns, or transform business data.");
    sb.AppendLine("- You may add/drop/rebuild performance-related objects and metadata, and you may change encapsulating objects such as views/procedures, but only if semantics remain unchanged.");
    sb.AppendLine("- If you alter a stored procedure or view, the revert script must restore the exact previous definition.");
    sb.AppendLine("- If you create a computed column, indexed view, or helper structure, the revert script must fully remove it.");
    sb.AppendLine("- If you use Query Store, plan guides, hints, scoped settings, compression, partitioning, or plan forcing, the revert script must fully undo those changes.");
    sb.AppendLine("- If you need to add an index or structure that conflicts with an existing constraint, handle it safely:");
    sb.AppendLine("  - Drop the conflicting constraint FIRST in the optimize script only if absolutely necessary");
    sb.AppendLine("  - Re-add and re-trust the constraint in the REVERT script");
    sb.AppendLine("- Prefer minimally invasive changes over broad or global database changes.");
    sb.AppendLine("- Avoid instance-level changes. Prefer object-level or database-level changes only when clearly justified.");
    sb.AppendLine("- All scripts must be idempotent where possible using IF EXISTS / IF NOT EXISTS checks.");
    sb.AppendLine("- Use the database name ONLY if explicitly provided as a three-part name.");
    sb.AppendLine("- If the schema information indicates the current compression state, do NOT suggest the same compression level again.");
    sb.AppendLine("- For compression changes, the revert must restore the original compression level exactly.");
    sb.AppendLine("- For partitioning changes, only suggest them if the table shape and workload clearly justify them, and the revert must fully restore the prior structure.");
    sb.AppendLine("- For plan forcing or plan guides, only suggest them if the scenario indicates plan instability, bad parameter sniffing, or a likely unstable optimizer choice.");
    sb.AppendLine("- For computed columns or indexed views, ensure determinism and schema binding where required.");
    sb.AppendLine("- Do NOT propose a semantic rewrite of the benchmark query text itself unless the query is executed through a stored procedure or view that can be safely rewritten without changing results.");
    sb.AppendLine();
    sb.AppendLine("## Selection Guidance");
    sb.AppendLine("- Prefer the single optimization with the highest expected payoff and acceptable operational risk.");
    sb.AppendLine("- Use the schema information to avoid suggesting objects that already exist.");
    sb.AppendLine("- If prior attempts show that a category already regressed, choose a different category.");
    sb.AppendLine("- If the workload appears analytical, consider columnstore, compression, indexed views, or partitioning.");
    sb.AppendLine("- If the workload appears highly selective, consider rowstore seek-oriented designs, filtered indexes, filtered statistics, or computed columns.");
    sb.AppendLine("- If the issue appears to be plan instability or parameter sensitivity, consider Query Store, plan guides, hints, or parameter-sniffing mitigation.");
    sb.AppendLine("- If the issue appears driven by non-sargable predicates or implicit conversion, consider computed columns, datatype alignment, or encapsulating object rewrites.");
    sb.AppendLine();
    sb.AppendLine("## T-SQL Notes");
    sb.AppendLine("- Table (heap) compression example: `ALTER TABLE [schema].[table] REBUILD WITH (DATA_COMPRESSION = PAGE);`");
    sb.AppendLine("- Named index compression example: `ALTER INDEX [index_name] ON [schema].[table] REBUILD WITH (DATA_COMPRESSION = PAGE);`");
    sb.AppendLine("- All indexes compression example: `ALTER INDEX ALL ON [schema].[table] REBUILD WITH (DATA_COMPRESSION = PAGE);`");
    sb.AppendLine("- Revert compression by rebuilding with the original compression level, such as `NONE`, `ROW`, or `PAGE`.");
    sb.AppendLine("- If you alter a module definition, include the full `CREATE OR ALTER` statement needed for optimize and the full prior definition in revert.");
    sb.AppendLine("- If you use Query Store plan forcing or plan guides, include all commands needed both to apply and to remove them.");
    sb.AppendLine();
    sb.AppendLine("## Required Response Format");
    sb.AppendLine("Respond with EXACTLY this JSON format and no markdown code fences:");
    sb.AppendLine(@"{");
    sb.AppendLine(@"  ""description"": ""Brief description of what this optimization does and why it is likely to help"",");
    sb.AppendLine(@"  ""optimize_sql"": ""Full SQL script to apply the optimization"",");
    sb.AppendLine(@"  ""revert_sql"": ""Full SQL script to completely undo the optimization""");
    sb.AppendLine(@"}");

    return sb.ToString();
}

    private string BuildFixPrompt(string failedSql, string errorMessage, string scriptType,
        string? optimizeSql = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a MSSQL 2025 performance optimization expert.");
        sb.AppendLine();
        sb.AppendLine($"## Problem");
        sb.AppendLine($"The following {scriptType} SQL script failed with an error. Please fix it.");
        sb.AppendLine();
        sb.AppendLine("## Failed SQL Script");
        sb.AppendLine("```sql");
        sb.AppendLine(failedSql);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Error Message");
        sb.AppendLine("```");
        sb.AppendLine(errorMessage);
        sb.AppendLine("```");

        if (scriptType == "revert" && optimizeSql != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Original Optimize SQL (this is what the revert needs to undo)");
            sb.AppendLine("```sql");
            sb.AppendLine(optimizeSql);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("## Important");
        sb.AppendLine("- Important: In two-part names like `[Prefix].[ObjectName]`, assume `Prefix` is the SCHEMA name, not the database name.");
        sb.AppendLine("- Handle constraint conflicts (temporarily drop/re-add constraints if needed)");
        sb.AppendLine("- Use IF EXISTS checks where possible");
        sb.AppendLine("- The revert must fully undo any optimize changes");
        sb.AppendLine();
        sb.AppendLine("## Required Response Format");
        sb.AppendLine("Respond with EXACTLY this JSON format (no markdown code fences, just raw JSON):");
        sb.AppendLine(@"{");
        sb.AppendLine(@"  ""description"": ""Brief description of the fix"",");
        sb.AppendLine(@"  ""optimize_sql"": ""Full corrected optimize SQL script"",");
        sb.AppendLine(@"  ""revert_sql"": ""Full corrected revert SQL script""");
        sb.AppendLine(@"}");

        return sb.ToString();
    }

  private string BuildCombinedPrompt(
    string beforeSql,
    string schemaInfo,
    BenchmarkResult beforeResult,
    string previousAttempts)
{
    var sb = new StringBuilder();
    sb.AppendLine("You are a MSSQL 2025 performance optimization expert.");
    sb.AppendLine();
    sb.AppendLine("## Final Task: Combine Successful Strategies");
    sb.AppendLine("Several optimization attempts have already been performed.");
    sb.AppendLine("Some improved performance, some regressed, and some failed.");
    sb.AppendLine("Create one ULTIMATE optimization script that combines the most effective compatible strategies identified so far.");
    sb.AppendLine();
    sb.AppendLine("Do NOT change business semantics or persisted business data.");
    sb.AppendLine("Any change that affects row counts, result meaning, or data integrity is invalid.");
    sb.AppendLine();
    sb.AppendLine("## SQL Query Being Optimized");
    sb.AppendLine("```sql");
    sb.AppendLine(beforeSql);
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("## Current Performance (baseline)");
    sb.AppendLine($"- Average CPU time: {beforeResult.AvgCpu:F0} ms");
    sb.AppendLine($"- Average Elapsed time: {beforeResult.AvgElapsed:F0} ms");
    sb.AppendLine();
    sb.AppendLine("## Database Schema Information");
    sb.AppendLine(schemaInfo);
    sb.AppendLine();
    sb.AppendLine("## Summary of All Previous Attempts");
    sb.AppendLine(previousAttempts);
    sb.AppendLine();
    sb.AppendLine("## Allowed Combination Space");
    sb.AppendLine("You may combine compatible successful techniques across these categories:");
    sb.AppendLine("- Rowstore indexing");
    sb.AppendLine("- Filtered or covering indexes");
    sb.AppendLine("- Columnstore strategy");
    sb.AppendLine("- Persisted computed columns");
    sb.AppendLine("- Indexed views");
    sb.AppendLine("- Compression");
    sb.AppendLine("- Partitioning or partition-aligned structures");
    sb.AppendLine("- Statistics / filtered statistics");
    sb.AppendLine("- Trusted constraints / uniqueness / datatype alignment");
    sb.AppendLine("- Query Store forcing / plan guides / hints / MAXDOP / memory grant control");
    sb.AppendLine("- Parameter sniffing mitigation");
    sb.AppendLine("- Stored procedure or view improvements");
    sb.AppendLine("- Temp/intermediate structure optimization inside procedures/views");
    sb.AppendLine("- Maintenance or rebuild steps required to realize the chosen design");
    sb.AppendLine();
    sb.AppendLine("## Instructions");
    sb.AppendLine("1. Analyze which previous techniques actually helped.");
    sb.AppendLine("2. Combine only those that are compatible and likely additive.");
    sb.AppendLine("3. Do NOT combine conflicting structures or duplicate access paths.");
    sb.AppendLine("4. Prefer a coherent design over stacking many marginal changes.");
    sb.AppendLine("5. The revert script must FULLY undo EVERYTHING in reverse-safe order.");
    sb.AppendLine("6. All scripts must be idempotent where possible.");
    sb.AppendLine("7. If altering a stored procedure or view, the revert must restore the exact prior definition.");
    sb.AppendLine("8. If using Query Store, plan guides, compression, partitioning, computed columns, or indexed views, revert must fully remove or restore them.");
    sb.AppendLine();
    sb.AppendLine("## Required Response Format");
    sb.AppendLine("Respond with EXACTLY this JSON format and no markdown code fences:");
    sb.AppendLine(@"{");
    sb.AppendLine(@"  ""description"": ""ULTIMATE COMBINED: Detailed description of what was combined and why"",");
    sb.AppendLine(@"  ""optimize_sql"": ""Full combined SQL script"",");
    sb.AppendLine(@"  ""revert_sql"": ""Full combined revert SQL script""");
    sb.AppendLine(@"}");

    return sb.ToString();
}

    private async Task<(string optimizeSql, string revertSql, string description)> CallOpenAiAsync(
        string apiKey, string prompt)
    {
        try
        {
            _log("  Calling OpenAI API...");
            var client = new ChatClient(_config.OpenAI.Model, apiKey);
            var completion = await client.CompleteChatAsync(prompt);

            var responseText = completion.Value.Content[0].Text;
            _log($"  OpenAI response received ({responseText.Length} chars)");

            // Try to parse JSON response
            // Strip markdown code fences if present
            responseText = responseText.Trim();
            if (responseText.StartsWith("```"))
            {
                var firstNewline = responseText.IndexOf('\n');
                if (firstNewline > 0)
                    responseText = responseText[(firstNewline + 1)..];
                if (responseText.EndsWith("```"))
                    responseText = responseText[..^3];
                responseText = responseText.Trim();
            }

            var json = JsonDocument.Parse(responseText);
            var root = json.RootElement;

            var description = root.GetProperty("description").GetString() ?? "";
            var optimizeSql = root.GetProperty("optimize_sql").GetString() ?? "";
            var revertSql = root.GetProperty("revert_sql").GetString() ?? "";

            return (optimizeSql, revertSql, description);
        }
        catch (Exception ex)
        {
            _log($"  ERROR calling OpenAI: {ex.Message}");
            return ("", "", $"Error: {ex.Message}");
        }
    }
}
