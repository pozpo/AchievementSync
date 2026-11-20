using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Enabler;

/// <summary>Wraps the bundled generate_emu_config.exe (GBE Fork / GSE Tools) so AchievementSync can
/// refresh Steam schema locally using locally stored credentials.</summary>
public sealed class SteamSchemaGeneratorService : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private string _generatorRoot;
    private readonly SteamCredentialStore _credentialStore = new();
    private readonly object _lock = new();
    private Process? _runningProcess;

    public SteamSchemaGeneratorService(string? generatorRoot = null)
    {
        _generatorRoot = ResolveGeneratorRoot(generatorRoot);
    }

    public string GeneratorRoot => _generatorRoot;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_generatorRoot) &&
        File.Exists(Path.Combine(_generatorRoot, "generate_emu_config.exe"));

    public bool CredentialsConfigured => _credentialStore.Read() is not null;

    public bool HasRefreshToken =>
        File.Exists(Path.Combine(_generatorRoot, "refresh_tokens.json")) ||
        !string.IsNullOrWhiteSpace(_credentialStore.Read()?.RefreshToken);

    public string? StoredUsername => _credentialStore.Read()?.Username;

    /// <summary>Writes the throwaway Steam credentials into Windows Credential Manager and local files.</summary>
    public void SaveCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Steam username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Steam password is required.", nameof(password));

        var trimmedUser = username.Trim();
        var trimmedPass = password.Trim();

        _credentialStore.Write(new SteamCredentials
        {
            Username = trimmedUser,
            Password = trimmedPass,
            LastLoginUtc = DateTime.UtcNow
        });

        if (Directory.Exists(_generatorRoot))
        {
            try
            {
                File.WriteAllLines(Path.Combine(_generatorRoot, "my_login.txt"), [trimmedUser, trimmedPass], Utf8WithoutBom);
            }
            catch { }
        }
    }

    /// <summary>Clears stored Steam credentials.</summary>
    public void ClearCredentials()
    {
        _credentialStore.Delete();
        try
        {
            var loginFile = Path.Combine(_generatorRoot, "my_login.txt");
            if (File.Exists(loginFile)) File.Delete(loginFile);
            var tokenFile = Path.Combine(_generatorRoot, "refresh_tokens.json");
            if (File.Exists(tokenFile)) File.Delete(tokenFile);
        }
        catch { }
    }

    /// <summary>Runs generate_emu_config.exe locally for a single AppID and returns the output root.</summary>
    public async Task<GeneratorRunResult> GenerateSchemaAsync(
        string appId,
        CancellationToken cancellationToken = default,
        IProgress<GeneratorProgress>? progress = null)
    {
        if (!IsConfigured)
            return new GeneratorRunResult(false,
                $"The generate_emu_config.exe runtime was not found at '{_generatorRoot}'. " +
                "The generator needs generate_emu_config.exe from schema_parse.",
                appId, null);

        if (!NumericSteamId(appId))
            return new GeneratorRunResult(false, "A valid numeric Steam AppID is required.", appId, null);

        lock (_lock)
        {
            if (_runningProcess != null && !_runningProcess.HasExited)
                return new GeneratorRunResult(false, "A generator run is already in progress.", appId, null);
        }

        var credentials = _credentialStore.Read();
        if (credentials is null && !File.Exists(Path.Combine(_generatorRoot, "refresh_tokens.json")))
            return new GeneratorRunResult(false,
                "Steam credentials have not been saved yet. Sign in with username and password before refreshing metadata.",
                appId, null);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(_generatorRoot, "generate_emu_config.exe"),
            WorkingDirectory = _generatorRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-aw", "-cdx", "-clean", "-token", "-skip_inv", appId })
            startInfo.ArgumentList.Add(argument);

        if (credentials is not null)
        {
            startInfo.EnvironmentVariables["GSE_CFG_USERNAME"] = credentials.Username;
            startInfo.EnvironmentVariables["GSE_CFG_PASSWORD"] = credentials.Password;
        }

        progress?.Report(new GeneratorProgress { Phase = "schemaParse", Current = 1, Total = 3, Percent = 25, Detail = $"Connecting to Steam for AppID {appId}..." });

        Process? process = null;
        try
        {
            lock (_lock)
            {
                if (_runningProcess != null && !_runningProcess.HasExited)
                    return new GeneratorRunResult(false, "A generator run is already in progress.", appId, null);
                process = new Process { StartInfo = startInfo };
                _runningProcess = process;
            }

            if (!process.Start())
                return new GeneratorRunResult(false,
                    "The metadata generator could not be started. Check that generate_emu_config.exe runs on this PC.",
                    appId, null);

            var readOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var readErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
                if (cancellationToken.IsCancellationRequested)
                    return new GeneratorRunResult(false, "Metadata refresh was canceled.", appId, null);
                return new GeneratorRunResult(false, "The generator run was interrupted.", appId, null);
            }

            var stdout = await readOutputTask.ConfigureAwait(false);
            var stderr = await readErrorTask.ConfigureAwait(false);

            progress?.Report(new GeneratorProgress { Phase = "schemaParse", Current = 2, Total = 3, Percent = 75, Detail = "Processing generated Steam schema..." });

            var outputDir = FindOutputDirectory(_generatorRoot, appId);
            var metadataFound = outputDir is not null &&
                (File.Exists(Path.Combine(outputDir, "steam_settings", "achievements.json")) ||
                 File.Exists(Path.Combine(outputDir, "achievements.json")));

            var exitCode = process.ExitCode;
            var success = exitCode == 0 && metadataFound;

            // Check if refresh_tokens.json was saved and update credential store
            var tokenPath = Path.Combine(_generatorRoot, "refresh_tokens.json");
            if (File.Exists(tokenPath) && credentials is not null)
            {
                try
                {
                    credentials.RefreshToken = File.ReadAllText(tokenPath);
                    _credentialStore.Write(credentials);
                }
                catch { }
            }

            progress?.Report(new GeneratorProgress { Phase = "schemaParse", Current = 3, Total = 3, Percent = 100, Detail = success ? "Schema generation complete." : "Finished with warnings." });

            return new GeneratorRunResult(success,
                success ? $"Fresh metadata generated for AppID {appId}." :
                    metadataFound ? $"Metadata found for AppID {appId} (generator exited with code {exitCode})." :
                    $"The generator exited with code {exitCode}; existing output will be used if available.",
                appId, outputDir);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new GeneratorRunResult(false, "Metadata refresh was canceled.", appId, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            var fallbackDir = FindOutputDirectory(_generatorRoot, appId);
            return new GeneratorRunResult(fallbackDir is not null,
                $"The metadata generator could not complete ({exception.GetType().Name}); existing output will be used if available.",
                appId, fallbackDir);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_runningProcess, process)) _runningProcess = null;
            }
            process?.Dispose();
        }
    }

    /// <summary>Finds the most recent generated output directory for an AppID.</summary>
    public string? FindOutputDirectory(string gameDirectoryOrRoot, string appId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidateRoots = new List<string>
        {
            Path.Combine(gameDirectoryOrRoot, "generate_emu_config", "output", appId),
            Path.Combine(gameDirectoryOrRoot, "generate_emu_config", "_OUTPUT", appId),
            Path.Combine(_generatorRoot, "output", appId),
            Path.Combine(_generatorRoot, "_OUTPUT", appId),
            Path.Combine(appData, "Achievements", "configs", "schema", "steam", appId),
            Path.Combine(appData, "Achievements", "tools", "schema_parse", "output", appId),
            Path.Combine("C:\\Games", appId, "generate_emu_config", "output", appId),
            Path.Combine("C:\\Games", appId, "generate_emu_config", "_OUTPUT", appId)
        };

        if (Directory.Exists(_generatorRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(_generatorRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    candidateRoots.Add(Path.Combine(directory, "output", appId));
                    candidateRoots.Add(Path.Combine(directory, "_OUTPUT", appId));
                }
            }
            catch { }
        }

        return candidateRoots
            .Where(candidate => Directory.Exists(candidate) &&
                (File.Exists(Path.Combine(candidate, "steam_settings", "achievements.json")) ||
                 File.Exists(Path.Combine(candidate, "achievements.json"))))
            .OrderByDescending(candidate =>
            {
                try
                {
                    var file = Path.Combine(candidate, "steam_settings", "achievements.json");
                    if (!File.Exists(file)) file = Path.Combine(candidate, "achievements.json");
                    return File.GetLastWriteTimeUtc(file);
                }
                catch { return DateTime.MinValue; }
            })
            .FirstOrDefault();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { if (_runningProcess != null && !_runningProcess.HasExited) _runningProcess.Kill(entireProcessTree: true); }
            catch { }
            _runningProcess?.Dispose();
            _runningProcess = null;
        }
    }

    private static string ResolveGeneratorRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return Path.GetFullPath(configured);
        var baseDirectory = AppContext.BaseDirectory;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "Achievements", "tools", "schema_parse"),
            Path.Combine(localAppData, "AchievementSync", "tools", "schema_parse"),
            @"C:\Program Files\Achievements\resources\tools\schema_parse",
            Path.Combine(baseDirectory, "generate_emu_config"),
            @"C:\Games\generate_emu_config",
            @"C:\AchievementSync\generate_emu_config"
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "generate_emu_config.exe")))
                return candidate;
        }

        // Auto-extract if archive exists in Program Files\Achievements
        var zipPath = @"C:\Program Files\Achievements\resources\tools\schema_parse.zip";
        var targetExtract = Path.Combine(localAppData, "AchievementSync", "tools", "schema_parse");
        if (File.Exists(zipPath) && !Directory.Exists(targetExtract))
        {
            try
            {
                Directory.CreateDirectory(targetExtract);
                ZipFile.ExtractToDirectory(zipPath, targetExtract, true);
                if (File.Exists(Path.Combine(targetExtract, "generate_emu_config.exe")))
                    return targetExtract;
            }
            catch { }
        }

        foreach (var candidate in candidates)
            if (Directory.Exists(candidate)) return candidate;

        return Path.Combine(baseDirectory, "generate_emu_config");
    }

    private static bool NumericSteamId(string value) =>
        value.Length is >= 1 and <= 12 && value.All(char.IsAsciiDigit) && value.Any(character => character != '0');
}

/// <summary>Result of running generate_emu_config.exe.</summary>
public sealed class GeneratorRunResult
{
    public bool Success { get; }
    public string Message { get; }
    public string AppId { get; }
    public string? OutputDirectory { get; }

    public GeneratorRunResult(bool success, string message, string appId, string? outputDirectory)
    {
        Success = success;
        Message = message;
        AppId = appId;
        OutputDirectory = outputDirectory;
    }
}

/// <summary>Progress reported during a generator run.</summary>
public sealed class GeneratorProgress
{
    public string Phase { get; init; } = string.Empty;
    public int Current { get; init; }
    public int Total { get; init; }
    public int Percent { get; init; }
    public string Detail { get; init; } = string.Empty;
}
