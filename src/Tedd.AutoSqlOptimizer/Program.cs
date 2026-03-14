using Tedd.AutoSqlOptimizer.Models;
using Tedd.AutoSqlOptimizer.Services;

using Microsoft.Extensions.Configuration;

namespace Tedd.AutoSqlOptimizer;

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        WriteColored("╔══════════════════════════════════════════╗", ConsoleColor.Cyan);
        WriteColored("║   MSSQL Optimization Benchmark Tool      ║", ConsoleColor.Cyan);
        WriteColored("╚══════════════════════════════════════════╝", ConsoleColor.Cyan);
        Console.WriteLine();

        // Load configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var config = new BenchmarkConfig();
        configuration.Bind(config);

        // Allow environment variable override for OpenAI key
        if (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
        {
            config.OpenAI.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrEmpty(config.OpenAI.ApiKey))
                config.OpenAI.ApiKey =
                    File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".openaikey"));
        }

        // Parse args for specific folder
        string? specificFolder = null;
        if (args.Length > 0)
        {
            specificFolder = args[0];
            WriteColored($"Filtering to optimizations matching: {specificFolder}", ConsoleColor.Yellow);
        }

        WriteColored($"Connection: {MaskConnectionString(config.ConnectionString)}", ConsoleColor.DarkGray);
        WriteColored($"Iterations: {config.BenchmarkIterations} (warm-up: {config.WarmUpIterations})", ConsoleColor.DarkGray);
        WriteColored($"AI Model: {config.OpenAI.Model}", ConsoleColor.DarkGray);
        WriteColored($"AI Optimizations: {config.AiOptimizationCount}, Max Retries: {config.AiMaxRetries}", ConsoleColor.DarkGray);
        Console.WriteLine();

        void Log(string message)
        {
            var color = message switch
            {
                var m when m.Contains("ERROR", StringComparison.OrdinalIgnoreCase) => ConsoleColor.Red,
                var m when m.Contains("WARNING", StringComparison.OrdinalIgnoreCase) => ConsoleColor.Yellow,
                var m when m.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) => ConsoleColor.DarkRed,
                var m when m.Contains("===") => ConsoleColor.Cyan,
                var m when m.Contains("---") => ConsoleColor.DarkCyan,
                var m when m.Contains("successfully") => ConsoleColor.Green,
                _ => ConsoleColor.Gray
            };
            WriteColored(message, color);
        }

        var runner = new BenchmarkRunner(config, Log);
        await runner.RunAsync(specificFolder);

        WriteColored("\nBenchmark complete!", ConsoleColor.Green);
    }

    private static void WriteColored(string message, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = prev;
    }

    private static string MaskConnectionString(string connStr)
    {
        // Mask password if present
        return System.Text.RegularExpressions.Regex.Replace(
            connStr, @"(Password|Pwd)\s*=\s*[^;]+",
            "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
