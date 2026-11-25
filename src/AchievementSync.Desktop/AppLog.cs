using System.IO;

namespace AchievementSync.Desktop;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync",
        "Logs",
        $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Action(string message) => Write("ACTION", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message, Exception exception) => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never prevent local scanning or opening the UI.
        }
    }
}
