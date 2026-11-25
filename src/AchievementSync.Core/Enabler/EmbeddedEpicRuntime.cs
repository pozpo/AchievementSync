using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded Epic Games (Nemirtingas Epic Emulator) runtime files.
/// Extracted on-demand to %LOCALAPPDATA%\AchievementSync\tools\epic_nemirtingas when the Epic emulator target is planned.
/// </summary>
public static class EmbeddedEpicRuntime
{
    private const string ResourcePrefix = "AchievementSync.Core.EpicTemplate.";
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "epic_nemirtingas");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("EOSSDK-Win64-Shipping.dll", ResourcePrefix + "EOSSDK-Win64-Shipping.dll"),
        ("EOSSDK-Win32-Shipping.dll", ResourcePrefix + "EOSSDK-Win32-Shipping.dll"),
        ("NemirtingasEpicEmu.json", ResourcePrefix + "NemirtingasEpicEmu.json")
    ];

    public static string RootPath => TargetDirectory;

    public static bool IsReady =>
        File.Exists(Path.Combine(TargetDirectory, "EOSSDK-Win64-Shipping.dll")) &&
        File.Exists(Path.Combine(TargetDirectory, "NemirtingasEpicEmu.json"));

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var rootPrefix = Path.GetFullPath(TargetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedEpicRuntime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TargetDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled Epic emulator file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled Epic emulator resource '{resourceName}' is missing.");
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
                    "The bundled Nemirtingas Epic Emulator files could not be extracted to LocalAppData.", exception);
            }
        }
    }
}
