using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AchievementSync.Desktop.Services;

public sealed record GitHubAppReleaseUpdate(
    Version Version,
    string Tag,
    string Name,
    string Body,
    Uri ReleasePage,
    Uri DownloadUri,
    long Size,
    string Sha256)
{
    public string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";
}

public sealed record UpdateDownloadProgress(long BytesDownloaded, long TotalBytes)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesDownloaded * 100 / TotalBytes, 0, 100);
}

/// <summary>Checks published GitHub releases and downloads the packaged Windows executable.</summary>
public sealed class GitHubReleaseUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/pozpo/AchievementSync/releases/latest";
    private const string ReleaseAssetPrefix = "/pozpo/AchievementSync/releases/download/";
    private const long MaximumExecutableBytes = 600L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementSync-Update/1.3.0");
        return client;
    }

    public async Task<GitHubAppReleaseUpdate?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag)
            throw new InvalidDataException("The latest GitHub release has no version tag.");

        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var releaseVersion))
            throw new InvalidDataException($"The latest GitHub release tag '{tag}' is not a numeric version.");
        if (releaseVersion <= currentVersion) return null;

        var releasePageText = root.TryGetProperty("html_url", out var releasePageElement)
            ? releasePageElement.GetString()
            : null;
        if (!Uri.TryCreate(releasePageText, UriKind.Absolute, out var releasePage)
            || releasePage.Scheme != Uri.UriSchemeHttps
            || !string.Equals(releasePage.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The latest GitHub release has an invalid release page URL.");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The latest GitHub release has no downloadable assets.");

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)
                || !string.Equals(nameElement.GetString(), "AchievementSync.exe", StringComparison.OrdinalIgnoreCase)) continue;

            var downloadText = asset.TryGetProperty("browser_download_url", out var downloadElement)
                ? downloadElement.GetString()
                : null;
            if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var downloadUri)
                || !IsTrustedReleaseAssetUrl(downloadUri))
                throw new InvalidDataException("The release executable does not use the expected GitHub download URL.");

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            if (size is <= 0 or > MaximumExecutableBytes)
                throw new InvalidDataException("The release executable has an invalid file size.");

            var digestText = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            const string digestPrefix = "sha256:";
            if (digestText is null || !digestText.StartsWith(digestPrefix, StringComparison.OrdinalIgnoreCase)
                || digestText.Length != digestPrefix.Length + 64
                || !digestText[digestPrefix.Length..].All(Uri.IsHexDigit))
                throw new InvalidDataException("The release executable does not include a valid SHA-256 digest.");

            var name = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? tag : tag;
            var body = root.TryGetProperty("body", out var bodyProperty) ? bodyProperty.GetString() ?? string.Empty : string.Empty;
            return new GitHubAppReleaseUpdate(
                releaseVersion,
                tag,
                name,
                body,
                releasePage,
                downloadUri,
                size,
                digestText[digestPrefix.Length..].ToLowerInvariant());
        }

        throw new InvalidDataException("The latest GitHub release does not include AchievementSync.exe.");
    }

    public async Task<string> DownloadAndVerifyAsync(
        GitHubAppReleaseUpdate update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsTrustedReleaseAssetUrl(update.DownloadUri))
            throw new InvalidDataException("The update download URL is not trusted.");

        var downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "Updates", "downloads");
        Directory.CreateDirectory(downloadDirectory);
        var destination = Path.Combine(downloadDirectory, $"AchievementSync-{update.VersionText}-{Guid.NewGuid():N}.exe");

        try
        {
            using var response = await Http.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.Size)
                throw new InvalidDataException("The downloaded executable size does not match the GitHub release metadata.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[256 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                downloaded += read;
                if (downloaded > update.Size || downloaded > MaximumExecutableBytes)
                    throw new InvalidDataException("The downloaded executable is larger than expected.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(downloaded, update.Size));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (downloaded != update.Size)
                throw new InvalidDataException("The downloaded executable is incomplete.");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("The downloaded executable failed its SHA-256 check.");

            await using var signatureStream = File.OpenRead(destination);
            if (signatureStream.ReadByte() != 'M' || signatureStream.ReadByte() != 'Z')
                throw new InvalidDataException("The downloaded file is not a Windows executable.");
            return destination;
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void LaunchReplacement(string runningExecutable, string downloadedExecutable, string versionText)
    {
        var targetPath = Path.GetFullPath(runningExecutable);
        if (!string.Equals(Path.GetFileName(targetPath), "AchievementSync.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install updates from the packaged AchievementSync.exe application.");
        if (!File.Exists(targetPath) || !File.Exists(downloadedExecutable))
            throw new FileNotFoundException("The running app or downloaded update could not be found.");

        var stagedPath = targetPath + ".update";
        File.Copy(downloadedExecutable, stagedPath, overwrite: true);

        var safeVersion = string.Concat(versionText.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var backupPath = targetPath + $".previous-{safeVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievementSync", "Updates");
        Directory.CreateDirectory(updateDirectory);
        var scriptPath = Path.Combine(updateDirectory, $"install-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, InstallerScript);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                     "-File", scriptPath, "-TargetPath", targetPath, "-StagedPath", stagedPath,
                     "-BackupPath", backupPath, "-ProcessId", Environment.ProcessId.ToString(),
                     "-LogPath", Path.Combine(updateDirectory, "last-install-error.txt")
                 })
            startInfo.ArgumentList.Add(argument);

        if (Process.Start(startInfo) is null)
        {
            try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException("The update installer could not be started.");
        }

    }

    private static bool IsTrustedReleaseAssetUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(ReleaseAssetPrefix, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith("/AchievementSync.exe", StringComparison.OrdinalIgnoreCase);

    private const string InstallerScript = """
        param(
            [Parameter(Mandatory=$true)][string]$TargetPath,
            [Parameter(Mandatory=$true)][string]$StagedPath,
            [Parameter(Mandatory=$true)][string]$BackupPath,
            [Parameter(Mandatory=$true)][int]$ProcessId,
            [Parameter(Mandatory=$true)][string]$LogPath
        )
        $ErrorActionPreference = 'Stop'
        try {
            $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($running) { $running.WaitForExit() }
            Start-Sleep -Milliseconds 500
            if (!(Test-Path -LiteralPath $StagedPath -PathType Leaf)) { throw 'The staged update file is missing.' }
            if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
                Move-Item -LiteralPath $TargetPath -Destination $BackupPath -Force
            }
            try {
                Move-Item -LiteralPath $StagedPath -Destination $TargetPath -Force
                Start-Process -FilePath $TargetPath
            }
            catch {
                if (Test-Path -LiteralPath $TargetPath -PathType Leaf) { Remove-Item -LiteralPath $TargetPath -Force }
                if (Test-Path -LiteralPath $BackupPath -PathType Leaf) { Move-Item -LiteralPath $BackupPath -Destination $TargetPath -Force }
                throw
            }
        }
        catch {
            try { Set-Content -LiteralPath $LogPath -Value $_.ToString() -Force } catch { }
            if (!(Test-Path -LiteralPath $TargetPath -PathType Leaf) -and (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
                try { Move-Item -LiteralPath $BackupPath -Destination $TargetPath -Force } catch { }
            }
        }
        """;
}
