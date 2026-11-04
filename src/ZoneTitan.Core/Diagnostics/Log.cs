using ZoneTitan.Core.Zones;

namespace ZoneTitan.Core.Diagnostics;

/// <summary>
/// Minimal rolling text log in %APPDATA%\ZoneTitan\logs. Deliberately dependency-free —
/// when a display apply goes wrong, the log is the only evidence of what the engine tried.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Also mirror log lines to the console. The CLI turns this on.</summary>
    public static bool EchoToConsole { get; set; }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {level}  {message}";

        if (EchoToConsole)
            Console.Error.WriteLine(line);

        try
        {
            lock (Gate)
            {
                ConfigStore.EnsureDirectories();
                var path = Path.Combine(
                    ConfigStore.LogDirectory, $"zonetitan-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never take the engine down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
