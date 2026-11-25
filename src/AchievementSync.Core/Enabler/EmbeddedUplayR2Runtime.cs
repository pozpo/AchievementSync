using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded Ubisoft Connect / Uplay R2 emulator runtime files (Goldberg Uplay R2).
/// Extracted on-demand to %LOCALAPPDATA%\AchievementSync\tools\uplay_r2 when the Uplay R2 target is planned.
/// </summary>
public static class EmbeddedUplayR2Runtime
{
    private const string ResourcePrefix = "AchievementSync.Core.UplayR2Template.";
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "uplay_r2");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("achievements_schema_example.json", ResourcePrefix + "achievements_schema_example.json"),
        ("upc_r2.ini", ResourcePrefix + "upc_r2.ini"),
        ("upc_r2_loader.dll", ResourcePrefix + "upc_r2_loader.dll"),
        ("upc_r2_loader64.dll", ResourcePrefix + "upc_r2_loader64.dll"),
        ("uplay_r2.ini", ResourcePrefix + "uplay_r2.ini"),
        ("uplay_r2_loader.dll", ResourcePrefix + "uplay_r2_loader.dll"),
        ("uplay_r2_loader64.dll", ResourcePrefix + "uplay_r2_loader64.dll")
    ];

    public static string RootPath => TargetDirectory;

    public static bool IsReady =>
        File.Exists(Path.Combine(TargetDirectory, "uplay_r2_loader64.dll")) &&
        File.Exists(Path.Combine(TargetDirectory, "uplay_r2.ini"));

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var rootPrefix = Path.GetFullPath(TargetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedUplayR2Runtime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TargetDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled Uplay R2 file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled Uplay R2 resource '{resourceName}' is missing.");
                    if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            resource.CopyTo(output);
                            output.Flush(flushToDisk: true);
                        }
                        File.Move(temporary, destination, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }

                return TargetDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "The bundled Uplay R2 files could not be extracted to LocalAppData.", exception);
            }
        }
    }
}
