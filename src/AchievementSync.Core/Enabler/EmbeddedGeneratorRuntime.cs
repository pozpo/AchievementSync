using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace AchievementSync.Core.Enabler;

/// <summary>
/// Manages the embedded Steam schema generator runtime (generate_emu_config.exe and dependencies).
/// The runtime is embedded within AchievementSync and auto-extracted to LocalAppData so users
/// never need to download or install external applications.
/// </summary>
public static class EmbeddedGeneratorRuntime
{
    private static readonly object SyncLock = new();
    private static readonly string ToolsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementSync", "tools", "schema_parse");

    public static string RootPath => ToolsDirectory;

    public static string ExecutablePath => Path.Combine(ToolsDirectory, "generate_emu_config.exe");

    public static string RefreshTokensPath => Path.Combine(ToolsDirectory, "refresh_tokens.json");

    public static string LoginFilePath => Path.Combine(ToolsDirectory, "my_login.txt");

    public static bool IsReady => File.Exists(ExecutablePath);

    /// <summary>
    /// Ensures that the internal schema_parse generator runtime is extracted and ready to run.
    /// </summary>
    public static string EnsureReady()
    {
        lock (SyncLock)
        {
            if (IsReady) return ToolsDirectory;

            Directory.CreateDirectory(ToolsDirectory);

            // 1. Extract from embedded resource
            try
            {
                var assembly = typeof(EmbeddedGeneratorRuntime).Assembly;
                using var resourceStream = assembly.GetManifestResourceStream("AchievementSync.Core.Resources.schema_parse.zip");
                if (resourceStream != null)
                {
                    using var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(ToolsDirectory, overwriteFiles: true);
                    if (IsReady) return ToolsDirectory;
                }
            }
            catch { }

            // 2. Fallback: check known local archive seeds
            var seedArchives = new[]
            {
                @"C:\Program Files\Achievements\resources\tools\schema_parse.zip",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Achievements", "tools", "schema_parse.zip")
            };

            foreach (var seed in seedArchives)
            {
                if (File.Exists(seed))
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(seed, ToolsDirectory, overwriteFiles: true);
                        if (IsReady) return ToolsDirectory;
                    }
                    catch { }
                }
            }

            // 3. Fallback: check existing local uncompressed tool folders
            var existingExtracts = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Achievements", "tools", "schema_parse"),
                @"C:\Program Files\Achievements\resources\tools\schema_parse"
            };

            foreach (var existing in existingExtracts)
            {
                if (Directory.Exists(existing) && File.Exists(Path.Combine(existing, "generate_emu_config.exe")))
                {
                    try
                    {
                        CopyDirectory(existing, ToolsDirectory);
                        if (IsReady) return ToolsDirectory;
                    }
                    catch { }
                }
            }

            return ToolsDirectory;
        }
    }

    /// <summary>
    /// Restores the refresh_tokens.json file from the Windows Credential Store if missing from disk.
    /// </summary>
    public static void RestoreRefreshToken(SteamCredentialStore credentialStore)
    {
        try
        {
            if (!File.Exists(RefreshTokensPath))
            {
                var credentials = credentialStore.Read();
                if (!string.IsNullOrWhiteSpace(credentials?.RefreshToken))
                {
                    Directory.CreateDirectory(ToolsDirectory);
                    File.WriteAllText(RefreshTokensPath, credentials.RefreshToken);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Saves the refresh_tokens.json file from disk into the Windows Credential Store.
    /// </summary>
    public static void SyncRefreshTokenToStore(SteamCredentialStore credentialStore)
    {
        try
        {
            if (File.Exists(RefreshTokensPath))
            {
                var tokenContent = File.ReadAllText(RefreshTokensPath);
                var credentials = credentialStore.Read();
                if (credentials != null && !string.IsNullOrWhiteSpace(tokenContent))
                {
                    credentials.RefreshToken = tokenContent;
                    credentialStore.Write(credentials);
                }
            }
        }
        catch { }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }
}
