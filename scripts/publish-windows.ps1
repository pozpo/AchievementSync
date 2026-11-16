param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.2",
    [string]$GitHubOAuthClientId = $env:ACHIEVEMENTSYNC_GITHUB_CLIENT_ID
)

$ErrorActionPreference = "Stop"
$rootPath = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $rootPath "src\AchievementSync.Desktop\AchievementSync.Desktop.csproj"
$outputPath = Join-Path $rootPath "dist\AchievementSync-$Version-$Runtime"

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $outputPath `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -p:AchievementSyncGitHubClientId=$GitHubOAuthClientId `
    -p:AssemblyVersion=${Version}.0 `
    -p:FileVersion=${Version}.0 `
    -p:InformationalVersion=$Version-preview `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $outputPath "AchievementSync.Desktop.exe"
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Publish completed without the expected executable: $executablePath"
}

Write-Output "Published AchievementSync $Version ($Runtime) to $outputPath"
