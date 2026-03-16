using System.Data.Common;

using Tedd.AutoSqlOptimizer.Models;

namespace Tedd.AutoSqlOptimizer.Services;

/// <summary>
/// Abstracts all database-engine-specific operations so the benchmarking
/// infrastructure can be used with different database back-ends.
/// </summary>
public interface IDatabaseExecutor
{
    /// <summary>Opens a new, ready-to-use connection to the database.</summary>
    Task<DbConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and executes a SQL script file (split on GO batches), optionally
    /// switching the initial catalog (e.g. to master for RESTORE DATABASE).
    /// </summary>
    Task ExecuteInitSqlAsync(string initSqlPath, string connectionString, Action<string> log, CancellationToken cancellationToken = default);

    /// <summary>Executes <paramref name="sql"/> and returns CPU + elapsed timing.</summary>
    SqlTimingResult ExecuteWithTiming(DbConnection conn, string sql);

    /// <summary>Updates table statistics so the query optimiser has accurate cardinality data.</summary>
    void UpdateStatistics(DbConnection conn);

    /// <summary>
    /// Clears the database engine's buffer / plan caches so subsequent benchmark
    /// runs are not influenced by previously cached data or plans.
    /// </summary>
    void ClearCache(DbConnection conn);

    /// <summary>Executes one or more GO-delimited SQL batches with no result set.</summary>
    void ExecuteNonQuery(DbConnection conn, string sql);

    /// <summary>Executes a query and returns the first column of the first row as a string.</summary>
    string ExecuteScalar(DbConnection conn, string sql);

    /// <summary>Executes a query and returns all rows as a list of name→value dictionaries.</summary>
    List<Dictionary<string, string>> ExecuteQuery(DbConnection conn, string sql);

    /// <summary>Returns the row count and a deterministic checksum for a single table.</summary>
    (long RowCount, long? Checksum, string Summary) ComputeTableChecksum(DbConnection conn, string schema, string table);

    /// <summary>Returns checksums for a collection of (schema, table) pairs.</summary>
    Dictionary<string, (long RowCount, long? Checksum, string Summary)> ComputeDataChecksums(
        DbConnection conn,
        IEnumerable<(string Schema, string Table)> tables);
}
