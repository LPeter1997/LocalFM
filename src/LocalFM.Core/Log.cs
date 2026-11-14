namespace LocalFM.Core;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>Minimal process-wide logging hook. Hosts install a sink; the default writes to stderr.</summary>
public static class Log
{
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static Action<LogLevel, string, string, Exception?> Sink { get; set; } = (level, area, msg, ex) =>
        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {area}: {msg}{(ex is null ? "" : " — " + ex)}");

    public static void Write(LogLevel level, string area, string message, Exception? ex = null)
    {
        if (level < MinimumLevel) return;
        try { Sink(level, area, message, ex); } catch { /* logging must never throw */ }
    }

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message);
    public static void Warn(string area, string message, Exception? ex = null) => Write(LogLevel.Warning, area, message, ex);
    public static void Error(string area, string message, Exception? ex = null) => Write(LogLevel.Error, area, message, ex);
}
