using System.Collections.Concurrent;

namespace JRemote.Core.Logging;

public static class AppLogger
{
    private static readonly ConcurrentQueue<string> LogsQueue = new();
    private static readonly object FileLock = new();
    private static string? _logFilePath;

    public static event Action<string>? LogAdded;

    public static void Initialize(string? customPath = null)
    {
        var dir = customPath ?? AppDomain.CurrentDomain.BaseDirectory;
        _logFilePath = Path.Combine(dir, "airplay_debug.log");
        try
        {
            File.AppendAllText(_logFilePath, $"\n--- JRemote Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n");
        }
        catch { }
    }

    public static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] {message}";

        LogsQueue.Enqueue(line);
        while (LogsQueue.Count > 500)
        {
            LogsQueue.TryDequeue(out _);
        }

        try
        {
            if (_logFilePath != null)
            {
                lock (FileLock)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
        }
        catch { }

        LogAdded?.Invoke(line);
    }

    public static IReadOnlyList<string> GetRecentLogs() => LogsQueue.ToArray();
}
