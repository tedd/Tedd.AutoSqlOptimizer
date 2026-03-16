namespace Tedd.AutoSqlOptimizer;

/// <summary>
/// Thread-safe console output manager.
/// Maintains a sticky status bar on the bottom line that survives log output above it.
/// All console writes must go through this class to avoid interleaving.
/// </summary>
public static class ConsoleDisplay
{
    private static readonly object _lock = new();
    private static string _currentStatus = "";
    private static bool _statusVisible = false;
    private static bool _isRedirected;

    // Spinner frames for animated "running" indicator
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static int _spinnerIndex = 0;

    static ConsoleDisplay()
    {
        try { _isRedirected = Console.IsOutputRedirected; }
        catch { _isRedirected = true; }
    }

    /// <summary>Write a log line, preserving the sticky status bar below it.</summary>
    public static void WriteLine(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_lock)
        {
            EraseStatus();
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = prev;
            RedrawStatus();
        }
    }

    /// <summary>Update the sticky status bar at the bottom of the console (in-place).</summary>
    public static void SetStatus(string status, ConsoleColor color = ConsoleColor.DarkGray)
    {
        if (_isRedirected) return;
        lock (_lock)
        {
            _currentStatus = status;
            InternalRedraw(color);
        }
    }

    /// <summary>Advance spinner and update the sticky status bar.</summary>
    public static string NextSpinner()
    {
        _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
        return SpinnerFrames[_spinnerIndex];
    }

    /// <summary>Remove the sticky status bar.</summary>
    public static void ClearStatus()
    {
        if (_isRedirected) return;
        lock (_lock)
        {
            EraseStatus();
            _currentStatus = "";
            _statusVisible = false;
        }
    }

    /// <summary>
    /// Print a multi-line block (e.g. a status board) above the sticky bar.
    /// Each line is printed with the given color.
    /// </summary>
    public static void PrintBlock(IEnumerable<(string text, ConsoleColor color)> lines)
    {
        lock (_lock)
        {
            EraseStatus();
            foreach (var (text, color) in lines)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(text);
                Console.ForegroundColor = prev;
            }
            RedrawStatus();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void EraseStatus()
    {
        if (!_statusVisible || _isRedirected) return;
        try
        {
            var width = SafeWindowWidth();
            Console.Write("\r" + new string(' ', width - 1) + "\r");
        }
        catch { /* non-interactive console */ }
        _statusVisible = false;
    }

    private static void RedrawStatus()
    {
        if (string.IsNullOrEmpty(_currentStatus) || _isRedirected) return;
        InternalRedraw(ConsoleColor.DarkGray);
    }

    private static void InternalRedraw(ConsoleColor color)
    {
        if (_isRedirected) return;
        try
        {
            var width = SafeWindowWidth();
            var line = _currentStatus.Length >= width
                ? _currentStatus[..(width - 1)]
                : _currentStatus + new string(' ', width - 1 - _currentStatus.Length);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write("\r" + line);
            Console.ForegroundColor = prev;
            _statusVisible = true;
        }
        catch { /* non-interactive console */ }
    }

    private static int SafeWindowWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 80; }
    }

    // ── formatting helpers ────────────────────────────────────────────────────

    public static string ProgressBar(int done, int total, int barWidth = 20)
    {
        if (total <= 0) return new string('░', barWidth);
        var filled = (int)Math.Round((double)done / total * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);
        return new string('█', filled) + new string('░', barWidth - filled);
    }

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
