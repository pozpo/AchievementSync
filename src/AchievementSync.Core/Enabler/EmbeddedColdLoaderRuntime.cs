using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded ColdLoader runtime files (coldloader.dll, version.dll proxy, and coldloader.ini).
/// Used for games (e.g., Phantom Blade Zero) that require a direct DLL loader hook rather than
/// the standalone ColdClient launcher executable.
/// </summary>
public static class EmbeddedColdLoaderRuntime
{
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "coldloader");

    public static string RootPath => TargetDirectory;
    public static string ColdLoaderDllPath => Path.Combine(TargetDirectory, "coldloader.dll");
    public static string VersionProxyPath => Path.Combine(TargetDirectory, "version.dll");
    public static string ColdLoaderIniPath => Path.Combine(TargetDirectory, "coldloader.ini");

    public static bool IsReady => File.Exists(ColdLoaderDllPath) && File.Exists(VersionProxyPath) && File.Exists(ColdLoaderIniPath);

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var assembly = typeof(EmbeddedColdLoaderRuntime).Assembly;
                ExtractResource(assembly, "AchievementSync.Core.ColdLoaderTemplate.coldloader.dll", ColdLoaderDllPath);
                ExtractResource(assembly, "AchievementSync.Core.ColdLoaderTemplate.version.dll", VersionProxyPath);
                ExtractResource(assembly, "AchievementSync.Core.ColdLoaderTemplate.coldloader.ini", ColdLoaderIniPath);
                return TargetDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                throw new InvalidOperationException("Failed to unpack ColdLoader runtime.", exception);
            }
        }
    }

    private static void ExtractResource(System.Reflection.Assembly assembly, string resourceName, string destination)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        if (File.Exists(destination) && new FileInfo(destination).Length == stream.Length) return;

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
