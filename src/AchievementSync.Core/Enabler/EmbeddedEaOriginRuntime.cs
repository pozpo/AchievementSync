using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded EA / Origin emulator (anadius) runtime files.
/// Extracted on-demand to %LOCALAPPDATA%\AchievementSync\tools\ea_origin when the EA/Origin target is planned.
/// </summary>
public static class EmbeddedEaOriginRuntime
{
    private const string ResourcePrefix = "AchievementSync.Core.EaOriginTemplate.";
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "ea_origin");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("anadius64.dll", ResourcePrefix + "anadius64.dll"),
        ("anadius32.dll", ResourcePrefix + "anadius32.dll"),
        ("anadius64online.dll", ResourcePrefix + "anadius64online.dll"),
        ("anadius32online.dll", ResourcePrefix + "anadius32online.dll"),
        ("data.yaml", ResourcePrefix + "data.yaml")
    ];

    public static string RootPath => TargetDirectory;

    public static bool IsReady =>
        File.Exists(Path.Combine(TargetDirectory, "anadius64.dll")) &&
        File.Exists(Path.Combine(TargetDirectory, "anadius32.dll"));

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var rootPrefix = Path.GetFullPath(TargetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedEaOriginRuntime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TargetDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled EA/Origin emulator file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled EA/Origin resource '{resourceName}' is missing.");
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
                    "The bundled EA/Origin emulator files could not be extracted to LocalAppData.", exception);
            }
        }
    }
}
