namespace Tedd.AutoSqlOptimizer.Models;

public class OptimizationFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string BeforeSql { get; set; } = "";
    public string OptimizeSql { get; set; } = "";
    public string AfterSql { get; set; } = "";
    public string RevertSql { get; set; } = "";
    public bool IsAiMode => string.IsNullOrWhiteSpace(OptimizeSql);

    public static OptimizationFolder Load(string folderPath)
    {
        var name = System.IO.Path.GetFileName(folderPath);
        return new OptimizationFolder
        {
            Name = name,
            Path = folderPath,
            BeforeSql = ReadAndCleanSql(System.IO.Path.Combine(folderPath, "1_before.sql")),
            OptimizeSql = ReadAndCleanSql(System.IO.Path.Combine(folderPath, "2_optimize.sql")),
            AfterSql = ReadAndCleanSql(System.IO.Path.Combine(folderPath, "3_after.sql")),
            RevertSql = ReadAndCleanSql(System.IO.Path.Combine(folderPath, "4_revert.sql")),
        };
    }

    private static string ReadAndCleanSql(string filePath)
    {
        if (!File.Exists(filePath)) return "";
        var sql = File.ReadAllText(filePath).Trim();
        // Strip out SET STATISTICS TIME ON/OFF and cache-clearing commands
        // since the app handles these programmatically
        sql = System.Text.RegularExpressions.Regex.Replace(sql,
            @"SET\s+STATISTICS\s+TIME\s+(ON|OFF)\s*;?",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        sql = System.Text.RegularExpressions.Regex.Replace(sql,
            @"CHECKPOINT\s*;\s*DBCC\s+DROPCLEANBUFFERS\s*;\s*DBCC\s+FREEPROCCACHE\s*;?",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        // Remove standalone GO statements
        sql = System.Text.RegularExpressions.Regex.Replace(sql,
            @"^\s*GO\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
        return sql;
    }
}
