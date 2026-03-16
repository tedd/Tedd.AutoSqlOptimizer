using System.Data.Common;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;

using Tedd.AutoSqlOptimizer.Models;

namespace Tedd.AutoSqlOptimizer.Services;

/// <summary>
/// MSSQL-specific implementation of <see cref="IDatabaseExecutor"/>.
/// Handles CHECKPOINT / DBCC cache clearing, SET STATISTICS TIME timing capture,
/// sp_MSforeachtable statistics updates, and all other SQL Server particulars.
/// </summary>
public class MsSqlExecutor : IDatabaseExecutor
{
    private static readonly int _timeoutSeconds = 1200;
    private static readonly int _sleepBetweenExecuteMs = 500;
    private static readonly Regex TimingRegex = new(
        @"SQL Server Execution Times:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Action<string> _log;

    public MsSqlExecutor(Action<string> log)
    {
        _log = log;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public async Task<DbConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // ── Init SQL ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads and executes <paramref name="initSqlPath"/> against SQL Server,
    /// splitting on GO batches. Automatically switches to <c>master</c> if the
    /// script contains a RESTORE DATABASE statement.
    /// </summary>
    public async Task ExecuteInitSqlAsync(
        string initSqlPath,
        string connectionString,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        log($"\n--- Executing init.sql ({initSqlPath}) ---");
        var initSql = await File.ReadAllTextAsync(initSqlPath, cancellationToken);

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (initSql.Contains("RESTORE DATABASE", StringComparison.OrdinalIgnoreCase))
            builder.InitialCatalog = "master";

        using var initConn = new SqlConnection(builder.ConnectionString);
        await initConn.OpenAsync(cancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batches = Regex.Split(
            initSql, @"^\s*GO\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = batch.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            log($"[DEBUG SQL] Executing init batch:\n{trimmed}");
            var batchSw = System.Diagnostics.Stopwatch.StartNew();
            using var cmd = new SqlCommand(trimmed, initConn);
            cmd.CommandTimeout = _timeoutSeconds;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            batchSw.Stop();
            log($"[DEBUG SQL] Batch executed in {batchSw.ElapsedMilliseconds} ms");
        }

        sw.Stop();
        log($"--- init.sql execution completed in {sw.ElapsedMilliseconds} ms ---\n");
    }

    // ── Timing ────────────────────────────────────────────────────────────────

    public SqlTimingResult ExecuteWithTiming(DbConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteWithTiming:\n{sql}");
        var sqlConn = (SqlConnection)conn;
        var result = new SqlTimingResult();
        var messages = new List<string>();

        void InfoHandler(object sender, SqlInfoMessageEventArgs e) => messages.Add(e.Message);

        var batches = SplitOnGo(sql);

        sqlConn.InfoMessage += InfoHandler;
        try
        {
            using (var cmdOn = new SqlCommand("SET STATISTICS TIME ON;", sqlConn))
            {
                cmdOn.CommandTimeout = _timeoutSeconds;
                cmdOn.ExecuteNonQuery();
            }

            foreach (var batch in batches)
            {
                using var cmd = new SqlCommand(batch, sqlConn);
                cmd.CommandTimeout = _timeoutSeconds;
                cmd.ExecuteNonQuery();
            }

            using (var cmdOff = new SqlCommand("SET STATISTICS TIME OFF;", sqlConn))
            {
                cmdOff.CommandTimeout = _timeoutSeconds;
                cmdOff.ExecuteNonQuery();
            }
        }
        finally
        {
            sqlConn.InfoMessage -= InfoHandler;
        }

        // Parse timing from InfoMessage output and accumulate across all GO batches
        foreach (var msg in messages)
        {
            _log($"  [InfoMessage] {msg}");
            var matches = TimingRegex.Matches(msg);
            foreach (Match match in matches)
            {
                result.CpuTimeMs += int.Parse(match.Groups[1].Value);
                result.ElapsedTimeMs += int.Parse(match.Groups[2].Value);
            }
        }

        return result;
    }

    // ── MSSQL cache / statistics ──────────────────────────────────────────────

    public void UpdateStatistics(DbConnection conn)
    {
        _log("  Updating statistics (UPDATE STATISTICS WITH FULLSCAN)...");
        var sql = "EXEC sp_MSforeachtable 'UPDATE STATISTICS ? WITH FULLSCAN';";
        using var cmd = new SqlCommand(sql, (SqlConnection)conn);
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Flushes dirty buffer-pool pages to disk (CHECKPOINT), then drops clean
    /// buffers and the plan cache (DBCC DROPCLEANBUFFERS / FREEPROCCACHE).
    /// </summary>
    public void ClearCache(DbConnection conn)
    {
        _log("  Flushing (CHECKPOINT)...");
        var sql = @"CHECKPOINT;

DECLARE @StartTime datetime2(3) = SYSDATETIME();
DECLARE @TimedOut bit = 0;

WHILE EXISTS
(
    SELECT 1
    FROM sys.dm_os_buffer_descriptors
    WHERE database_id = DB_ID()
      AND is_modified = 1
)
BEGIN
    CHECKPOINT;

    IF DATEDIFF(SECOND, @StartTime, SYSDATETIME()) >= 60
    BEGIN
        SET @TimedOut = 1;
        BREAK;
    END;

    WAITFOR DELAY '00:00:00.250';
END

IF @TimedOut = 1
    THROW 50000, 'Timed out waiting for modified pages to flush after 60 seconds.', 1;
";
        using var cmd1 = new SqlCommand(sql, (SqlConnection)conn);
        cmd1.CommandTimeout = _timeoutSeconds;
        cmd1.ExecuteNonQuery();

        _log("  Clearing cache (DROPCLEANBUFFERS, FREEPROCCACHE)...");
        sql = @"DBCC DROPCLEANBUFFERS; DBCC FREEPROCCACHE;";
        using var cmd2 = new SqlCommand(sql, (SqlConnection)conn);
        cmd2.CommandTimeout = _timeoutSeconds;
        cmd2.ExecuteNonQuery();

        Thread.Sleep(_sleepBetweenExecuteMs);
    }

    // ── General execution ─────────────────────────────────────────────────────

    public void ExecuteNonQuery(DbConnection conn, string sql)
    {
        foreach (var batch in SplitOnGo(sql))
        {
            _log($"[DEBUG SQL] ExecuteNonQuery batch:\n{batch}");
            using var cmd = new SqlCommand(batch, (SqlConnection)conn);
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.ExecuteNonQuery();
        }
    }

    public string ExecuteScalar(DbConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteScalar:\n{sql}");
        var batches = SplitOnGo(sql);
        object? result = null;
        foreach (var batch in batches)
        {
            using var cmd = new SqlCommand(batch, (SqlConnection)conn);
            cmd.CommandTimeout = _timeoutSeconds;
            result = cmd.ExecuteScalar();
        }
        return result?.ToString() ?? "";
    }

    public List<Dictionary<string, string>> ExecuteQuery(DbConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteQuery:\n{sql}");
        var results = new List<Dictionary<string, string>>();
        using var cmd = new SqlCommand(sql, (SqlConnection)conn);
        cmd.CommandTimeout = _timeoutSeconds;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
            results.Add(row);
        }
        return results;
    }

    // ── Data integrity ────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a checksum for a single table: row count + CHECKSUM_AGG(BINARY_CHECKSUM(*)).
    /// Returns a string like "rows=1234, checksum=-987654321".
    /// </summary>
    public (long RowCount, long? Checksum, string Summary) ComputeTableChecksum(DbConnection conn, string schema, string table)
    {
        var sql = $@"
SELECT
    COUNT_BIG(*) AS [RowCount],
    CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS [Checksum]
FROM [{schema}].[{table}] WITH (NOLOCK);";

        _log($"[Checksum] Computing checksum for [{schema}].[{table}]...");
        using var cmd = new SqlCommand(sql, (SqlConnection)conn);
        cmd.CommandTimeout = _timeoutSeconds;
        try
        {
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var rowCount = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                long? checksum = reader.IsDBNull(1) ? null : (long)reader.GetInt32(1);
                var summary = $"rows={rowCount}, checksum={checksum?.ToString() ?? "NULL"}";
                _log($"[Checksum]   [{schema}].[{table}]: {summary}");
                return (rowCount, checksum, summary);
            }
        }
        catch (Exception ex)
        {
            _log($"[Checksum]   ERROR for [{schema}].[{table}]: {ex.Message}");
        }
        return (0, null, "error");
    }

    /// <summary>
    /// Computes checksums for a list of (schema, table) pairs.
    /// Returns a dictionary keyed by "schema.table" -> checksum summary string.
    /// </summary>
    public Dictionary<string, (long RowCount, long? Checksum, string Summary)> ComputeDataChecksums(
        DbConnection conn,
        IEnumerable<(string Schema, string Table)> tables)
    {
        var results = new Dictionary<string, (long, long?, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schema, table) in tables)
        {
            var key = $"{schema}.{table}";
            results[key] = ComputeTableChecksum(conn, schema, table);
        }
        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> SplitOnGo(string sql)
    {
        var batches = Regex.Split(sql, @"^\s*GO\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return batches
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
    }
}
