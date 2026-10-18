using System;
using System.IO;

namespace HandBrakeTreemap.Services;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Tiny application log: lines go to the log panel (via <see cref="Line"/>) and to
/// %LOCALAPPDATA%\HandBrakeTreemap\log.txt. Thread-safe; callable from any thread.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Raised for every line (on the calling thread — marshal to the UI yourself).</summary>
    public static event Action<string>? Line;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandBrakeTreemap", "log.txt");

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var prefix = level switch { LogLevel.Warn => "WAARSCHUWING ", LogLevel.Error => "FOUT ", _ => "" };
        var line = $"{DateTime.Now:HH:mm:ss}  {prefix}{message}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // Keep the file from growing forever.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }

        try { Line?.Invoke(line); } catch { }
    }
}
