using System.Security;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Extracts the bundled ColdClient launch files to LocalAppData. Only the fixed files listed
/// here are materialized; examples and user credentials are never included in the payload.
/// </summary>
public static class EmbeddedColdClientRuntime
{
    private const string ResourcePrefix = "AchievementSync.Core.ColdClientTemplate.";
    private static readonly object SyncLock = new();
    private static readonly string TemplateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "coldclient", "gbe-vs26-v1");

    private static readonly (string RelativePath, string ResourceName)[] BundledFiles =
    [
        ("Achievement-Enabler-LICENSE.txt", ResourcePrefix + "Achievement-Enabler-LICENSE.txt"),
        ("Achievement-Enabler-README.md", ResourcePrefix + "Achievement-Enabler-README.md"),
        ("ColdClientLoader.ini", ResourcePrefix + "ColdClientLoader.ini"),
        ("GameOverlayRenderer.dll", ResourcePrefix + "GameOverlayRenderer.dll"),
        ("GameOverlayRenderer64.dll", ResourcePrefix + "GameOverlayRenderer64.dll"),
        ("Goldberg-CREDITS.md", ResourcePrefix + "Goldberg-CREDITS.md"),
        ("Goldberg-README.experimental_steamclient.md", ResourcePrefix + "Goldberg-README.experimental_steamclient.md"),
        ("steamclient.dll", ResourcePrefix + "steamclient.dll"),
        ("steamclient64.dll", ResourcePrefix + "steamclient64.dll"),
        ("steamclient_loader_x64.exe", ResourcePrefix + "steamclient_loader_x64.exe"),
        ("steamclient_loader_x86.exe", ResourcePrefix + "steamclient_loader_x86.exe"),
        (Path.Combine("extra_dlls", "steamstub_x32.dll"), ResourcePrefix + "extra_dlls.steamstub_x32.dll"),
        (Path.Combine("extra_dlls", "steamstub_x64.dll"), ResourcePrefix + "extra_dlls.steamstub_x64.dll")
    ];

    private static readonly string[] RequiredFiles =
    [
        "ColdClientLoader.ini",
        "GameOverlayRenderer.dll",
        "GameOverlayRenderer64.dll",
        "steamclient.dll",
        "steamclient64.dll",
        "steamclient_loader_x64.exe",
        "steamclient_loader_x86.exe",
        Path.Combine("extra_dlls", "steamstub_x32.dll"),
        Path.Combine("extra_dlls", "steamstub_x64.dll")
    ];

    public static string RootPath => TemplateDirectory;

    public static string GameOverlayRenderer64Path => Path.Combine(TemplateDirectory, "GameOverlayRenderer64.dll");
    public static string SteamClient64Path => Path.Combine(TemplateDirectory, "steamclient64.dll");

    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            try
            {
                Directory.CreateDirectory(TemplateDirectory);
                var rootPrefix = Path.GetFullPath(TemplateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var assembly = typeof(EmbeddedColdClientRuntime).Assembly;

                foreach (var (relativePath, resourceName) in BundledFiles)
                {
                    var destination = Path.GetFullPath(Path.Combine(TemplateDirectory, relativePath));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A bundled ColdClient file resolved outside its extraction folder.");

                    using var resource = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException($"The bundled ColdClient resource '{resourceName}' is missing.");
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

                if (!RequiredFiles.All(relativePath => File.Exists(Path.Combine(TemplateDirectory, relativePath))))
                    throw new IOException("The bundled ColdClient payload is incomplete after extraction.");

                return TemplateDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "The bundled ColdClient files could not be extracted to LocalAppData. Check that the AchievementSync tools folder is writable, then try again.",
                    exception);
            }
        }
    }
}
