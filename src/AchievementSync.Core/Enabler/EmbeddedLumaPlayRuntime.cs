using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded LumaPlay 2.2.6 (x64 &amp; x86) Ubisoft Connect emulator runtime files.
/// Extracted on-demand to %LOCALAPPDATA%\AchievementSync\tools\lumaplay when the LumaPlay target is planned.
/// </summary>
public static class EmbeddedLumaPlayRuntime
{
    private const string ResourcePrefix = "AchievementSync.Core.LumaPlayTemplate.";
    private static readonly object SyncLock = new();
    private static readonly string TargetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "lumaplay");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("LumaPlay.txt", ResourcePrefix + "LumaPlay.txt"),
        ("LumaPlay_x64.exe", ResourcePrefix + "LumaPlay_x64.exe"),
        ("LumaPlay_x86.exe", ResourcePrefix + "LumaPlay_x86.exe"),
        (Path.Combine("x64", "LumaPlay.exe"), ResourcePrefix + "LumaPlay_x64.exe"),
        (Path.Combine("x86", "LumaPlay.exe"), ResourcePrefix + "LumaPlay_x86.exe"),
        (Path.Combine("LumaPlayFiles", "LumaPlay.ini"), ResourcePrefix + "LumaPlayFiles.LumaPlay.ini"),
        (Path.Combine("LumaPlayFiles", "LumaPlay_x64.dll"), ResourcePrefix + "LumaPlayFiles.LumaPlay_x64.dll"),
        (Path.Combine("LumaPlayFiles", "LumaPlay_x86.dll"), ResourcePrefix + "LumaPlayFiles.LumaPlay_x86.dll"),
        (Path.Combine("LumaPlayFiles", "LumaPlaySounds", "AchievementUnlocked.wav"), ResourcePrefix + "LumaPlayFiles.LumaPlaySounds.AchievementUnlocked.wav")
    ];

    public static string RootPath => TargetDirectory;

    public static bool IsReady =>
        (File.Exists(Path.Combine(TargetDirectory, "LumaPlay_x64.exe")) || File.Exists(Path.Combine(TargetDirectory, "x64", "LumaPlay.exe")))
        && (File.Exists(Path.Combine(TargetDirectory, "LumaPlay_x86.exe")) || File.Exists(Path.Combine(TargetDirectory, "x86", "LumaPlay.exe")))
        && File.Exists(Path.Combine(TargetDirectory, "LumaPlayFiles", "LumaPlay_x64.dll"))
        && File.Exists(Path.Combine(TargetDirectory, "LumaPlayFiles", "LumaPlay_x86.dll"))
        && File.Exists(Path.Combine(TargetDirectory, "LumaPlayFiles", "LumaPlay.ini"));

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TargetDirectory);
                var rootPrefix = Path.GetFullPath(TargetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedLumaPlayRuntime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TargetDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled LumaPlay file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled LumaPlay resource '{resourceName}' is missing.");
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
                    "The bundled LumaPlay files could not be extracted to LocalAppData.", exception);
            }
        }
    }
}
