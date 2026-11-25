using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded GOG UniverseLAN emulator runtime files and data templates.
/// Extracted on-demand to %LOCALAPPDATA%\AchievementSync\tools\universallan when the UniverseLAN target is planned.
/// </summary>
public static class EmbeddedUniverseLanRuntime
{
    private const string ResourcePrefix = "AchievementSync.Core.UniverseLanTemplate.";
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "universallan");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("Galaxy.dll", ResourcePrefix + "Galaxy.dll"),
        ("Galaxy64.dll", ResourcePrefix + "Galaxy64.dll"),
        ("UniverseLANServer.exe", ResourcePrefix + "UniverseLANServer.exe"),
        ("UniverseLANServer64.exe", ResourcePrefix + "UniverseLANServer64.exe"),
        ("UniverseLAN.ini", ResourcePrefix + "UniverseLAN.ini"),
        (Path.Combine("UniverseLANData", "Achievements.ini"), ResourcePrefix + "UniverseLANData.Achievements.ini"),
        (Path.Combine("UniverseLANData", "Config.ini"), ResourcePrefix + "UniverseLANData.Config.ini"),
        (Path.Combine("UniverseLANData", "DLC.ini"), ResourcePrefix + "UniverseLANData.DLC.ini"),
        (Path.Combine("UniverseLANData", "Stats.ini"), ResourcePrefix + "UniverseLANData.Stats.ini"),
        (Path.Combine("UniverseLANData", "UserData.ini"), ResourcePrefix + "UniverseLANData.UserData.ini"),
        (Path.Combine("UniverseLANData", "Avatars", "files", "me.png"), ResourcePrefix + "UniverseLANData.Avatars.files.me.png"),
        (Path.Combine("UniverseLANServerData", "Config.ini"), ResourcePrefix + "UniverseLANServerData.Config.ini"),
        (Path.Combine("UniverseLANServerData", "Avatars", "me.png"), ResourcePrefix + "UniverseLANServerData.Avatars.me.png")
    ];

    public static string RootPath => TargetDirectory;

    public static bool IsReady =>
        File.Exists(Path.Combine(TargetDirectory, "Galaxy64.dll")) &&
        File.Exists(Path.Combine(TargetDirectory, "UniverseLAN.ini"));

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var rootPrefix = Path.GetFullPath(TargetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedUniverseLanRuntime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TargetDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled UniverseLAN file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled UniverseLAN resource '{resourceName}' is missing.");
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
                    "The bundled UniverseLAN files could not be extracted to LocalAppData.", exception);
            }
        }
    }
}
