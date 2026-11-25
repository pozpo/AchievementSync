using System.IO;
using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded Goldberg regular steam_api emulator binaries (steam_api64.dll and steam_api.dll).
/// Automatically extracts them to LocalAppData so the app can replace game DLLs and configure steam_settings
/// without requiring any external tools.
/// </summary>
public static class EmbeddedRegularEmulatorRuntime
{
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "regular");

    public static string RootPath => TargetDirectory;
    public static string SteamApi64Path => Path.Combine(TargetDirectory, "steam_api64.dll");
    public static string SteamApi32Path => Path.Combine(TargetDirectory, "steam_api.dll");

    public static bool IsReady => File.Exists(SteamApi64Path) && File.Exists(SteamApi32Path);

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var assembly = typeof(EmbeddedRegularEmulatorRuntime).Assembly;
                ExtractResource(assembly, "AchievementSync.Core.RegularEmulator.steam_api64.dll", SteamApi64Path);
                ExtractResource(assembly, "AchievementSync.Core.RegularEmulator.steam_api.dll", SteamApi32Path);
                return TargetDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                throw new InvalidOperationException("Failed to unpack regular Goldberg emulator runtime.", exception);
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
