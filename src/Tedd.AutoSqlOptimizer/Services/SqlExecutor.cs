using Tedd.AutoSqlOptimizer.Models;

namespace Tedd.AutoSqlOptimizer.Services;

/// <summary>
/// Factory that returns the appropriate <see cref="IDatabaseExecutor"/> for the
/// configured database type.  Add new database back-ends here.
/// </summary>
public static class DatabaseExecutorFactory
{
    public static IDatabaseExecutor Create(BenchmarkConfig config, Action<string> log)
    {
        return config.DatabaseType.Trim().ToUpperInvariant() switch
        {
            "MSSQL" => new MsSqlExecutor(log),
            var t   => throw new NotSupportedException($"Database type '{t}' is not supported. Supported types: MSSQL")
        };
    }
}
