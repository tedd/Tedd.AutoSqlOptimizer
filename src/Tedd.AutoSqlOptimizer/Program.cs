using Tedd.AutoSqlOptimizer.Models;
using Tedd.AutoSqlOptimizer.Services;

using Microsoft.Extensions.Configuration;

namespace Tedd.AutoSqlOptimizer;

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ConsoleDisplay.WriteLine("╔══════════════════════════════════════════╗", ConsoleColor.Cyan);
        ConsoleDisplay.WriteLine("║   MSSQL Optimization Benchmark Tool      ║", ConsoleColor.Cyan);
        ConsoleDisplay.WriteLine("╚══════════════════════════════════════════╝", ConsoleColor.Cyan);
        ConsoleDisplay.WriteLine("", ConsoleColor.Gray);

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
            ConsoleDisplay.WriteLine($"Filtering to optimizations matching: {specificFolder}", ConsoleColor.Yellow);
        }

        ConsoleDisplay.WriteLine($"Connection: {MaskConnectionString(config.ConnectionString)}", ConsoleColor.DarkGray);
        ConsoleDisplay.WriteLine($"Iterations: {config.BenchmarkIterations} (warm-up: {config.WarmUpIterations})", ConsoleColor.DarkGray);
        ConsoleDisplay.WriteLine($"AI Model: {config.OpenAI.Model}", ConsoleColor.DarkGray);
        ConsoleDisplay.WriteLine($"AI Optimizations: {config.AiOptimizationCount}, Max Retries: {config.AiMaxRetries}", ConsoleColor.DarkGray);
        ConsoleDisplay.WriteLine("", ConsoleColor.Gray);

        void Log(string message)
        {
            var color = message switch
            {
                var m when m.Contains("ERROR", StringComparison.OrdinalIgnoreCase)    => ConsoleColor.Red,
                var m when m.Contains("WARNING", StringComparison.OrdinalIgnoreCase)  => ConsoleColor.Yellow,
                var m when m.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) => ConsoleColor.DarkRed,
                var m when m.Contains("===")    => ConsoleColor.Cyan,
                var m when m.Contains("---")    => ConsoleColor.DarkCyan,
                var m when m.Contains("successfully") => ConsoleColor.Green,
                _ => ConsoleColor.Gray
            };
            ConsoleDisplay.WriteLine(message, color);
        }

        // ── Ctrl+C / SIGTERM handler ──────────────────────────────────────────
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // don't terminate immediately — let RunAsync clean up
            if (!cts.IsCancellationRequested)
            {
                ConsoleDisplay.ClearStatus();
                ConsoleDisplay.WriteLine(
                    "\n⚠  Cancellation requested — finishing current step and writing reports…",
                    ConsoleColor.Yellow);
                cts.Cancel();
            }
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };

        // ── Run ───────────────────────────────────────────────────────────────
        var runner = new BenchmarkRunner(config, Log);
        await runner.RunAsync(specificFolder, cts.Token);

        ConsoleDisplay.ClearStatus();
        ConsoleDisplay.WriteLine("\n✓  Benchmark complete!", ConsoleColor.Green);
    }

    private static string MaskConnectionString(string connStr)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            connStr, @"(Password|Pwd)\s*=\s*[^;]+",
            "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
