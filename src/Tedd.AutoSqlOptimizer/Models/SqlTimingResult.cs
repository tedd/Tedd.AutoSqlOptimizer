namespace Tedd.AutoSqlOptimizer.Models;

public class SqlTimingResult
{
    public int CpuTimeMs { get; set; }
    public int ElapsedTimeMs { get; set; }

    public override string ToString() => $"CPU={CpuTimeMs}ms, Elapsed={ElapsedTimeMs}ms";
}
