using Tedd.AutoSqlOptimizer.Models;

using Microsoft.Data.SqlClient;

using System.Text.RegularExpressions;

namespace Tedd.AutoSqlOptimizer.Services;

public class SqlExecutor
{
    private static readonly int _timeoutSeconds = 1200;
    private static readonly int _sleepBetweenExecuteMs = 500;
    private static readonly Regex TimingRegex = new(
        @"SQL Server Execution Times:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Action<string> _log;

    public SqlExecutor(Action<string> log)
    {
        _log = log;
    }

    public SqlTimingResult ExecuteWithTiming(SqlConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteWithTiming:\n{sql}");
        var result = new SqlTimingResult();
        var messages = new List<string>();

        void InfoHandler(object sender, SqlInfoMessageEventArgs e)
        {
            messages.Add(e.Message);
        }

        conn.InfoMessage += InfoHandler;
        try
        {
            var wrappedSql = "SET STATISTICS TIME ON;\n" + sql + "\nSET STATISTICS TIME OFF;";
            using var cmd = new SqlCommand(wrappedSql, conn);
            cmd.CommandTimeout = _timeoutSeconds; // 10 minutes
            cmd.ExecuteNonQuery();
        }
        finally
        {
            conn.InfoMessage -= InfoHandler;
        }

        // Parse timing from InfoMessage output
        // We want the LAST "SQL Server Execution Times:" line (the actual execution, not parse/compile)
        foreach (var msg in messages)
        {
            _log($"  [InfoMessage] {msg}");
            var matches = TimingRegex.Matches(msg);
            foreach (Match match in matches)
            {
                result.CpuTimeMs = int.Parse(match.Groups[1].Value);
                result.ElapsedTimeMs = int.Parse(match.Groups[2].Value);
                // Keep overwriting — we want the last execution time
            }
        }

        return result;
    }

    public void UpdateStatistics(SqlConnection conn)
    {
        _log("  Updating statistics (UPDATE STATISTICS WITH FULLSCAN)...");
        var sql = "EXEC sp_MSforeachtable 'UPDATE STATISTICS ? WITH FULLSCAN';";
        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    public void ClearCache(SqlConnection conn)
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
        using var cmd1 = new SqlCommand(sql, conn);
        cmd1.CommandTimeout = _timeoutSeconds;
        cmd1.ExecuteNonQuery();

        _log("  Clearing cache (DROPCLEANBUFFERS, FREEPROCCACHE)...");
        sql = @"DBCC DROPCLEANBUFFERS; DBCC FREEPROCCACHE;";
        using var cmd2 = new SqlCommand(sql, conn);
        cmd2.CommandTimeout = _timeoutSeconds;
        cmd2.ExecuteNonQuery();

        Thread.Sleep(_sleepBetweenExecuteMs);
    }

    public void ExecuteNonQuery(SqlConnection conn, string sql)
    {
        // Split on GO statements for multi-batch scripts
        var batches = Regex.Split(sql, @"^\s*GO\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            _log($"[DEBUG SQL] ExecuteNonQuery batch:\n{trimmed}");
            using var cmd = new SqlCommand(trimmed, conn);
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.ExecuteNonQuery();
        }
    }

    public string ExecuteScalar(SqlConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteScalar:\n{sql}");
        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _timeoutSeconds;
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "";
    }

    public List<Dictionary<string, string>> ExecuteQuery(SqlConnection conn, string sql)
    {
        _log($"[DEBUG SQL] ExecuteQuery:\n{sql}");
        var results = new List<Dictionary<string, string>>();
        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _timeoutSeconds;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
            }
            results.Add(row);
        }
        return results;
    }

    /// <summary>
    /// Computes a checksum for a single table: row count + CHECKSUM_AGG(BINARY_CHECKSUM(*)).
    /// Returns a string like "rows=1234, checksum=-987654321".
    /// </summary>
    public (long RowCount, long? Checksum, string Summary) ComputeTableChecksum(SqlConnection conn, string schema, string table)
    {
        var sql = $@"
SELECT
    COUNT_BIG(*) AS [RowCount],
    CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS [Checksum]
FROM [{schema}].[{table}] WITH (NOLOCK);";

        _log($"[Checksum] Computing checksum for [{schema}].[{table}]...");
        using var cmd = new SqlCommand(sql, conn);
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
        SqlConnection conn,
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
}
