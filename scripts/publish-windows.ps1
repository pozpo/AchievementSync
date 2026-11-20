param(
    [string]$Runtime = "win-x64",
    [string]$GitHubOAuthClientId = $env:ACHIEVEMENTSYNC_GITHUB_CLIENT_ID
)

$ErrorActionPreference = "Stop"
$rootPath = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $rootPath "AchievementSync.sln"
$projectPath = Join-Path $rootPath "src\AchievementSync.Desktop\AchievementSync.Desktop.csproj"
$testProjectPath = Join-Path $rootPath "tests\AchievementSync.SmokeTests\AchievementSync.SmokeTests.csproj"
$outputPath = Join-Path $rootPath "output"
$stagePath = Join-Path $outputPath ".staging"
$stageExe = Join-Path $stagePath "AchievementSync.exe"
$outputExe = Join-Path $outputPath "AchievementSync.exe"
$backupExe = Join-Path $stagePath "AchievementSync.previous.exe"

if ($Runtime -ne "win-x64") {
    throw "AchievementSync currently publishes the Windows x64 runtime only."
}
if (Test-Path -LiteralPath $stagePath) {
    throw "A previous staging folder exists at '$stagePath'. Inspect it before running the publisher again."
}
$outputFullPath = [System.IO.Path]::GetFullPath($outputPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$stageFullPath = [System.IO.Path]::GetFullPath($stagePath)
if ($stageFullPath -ne (Join-Path $outputFullPath ".staging")) {
    throw "The staging path resolved outside the expected output directory."
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

& dotnet build $solutionPath --configuration Release -m:1
if ($LASTEXITCODE -ne 0) {
    throw "The Release solution build failed with exit code $LASTEXITCODE. The existing output executable was not changed."
}

& dotnet run --project $testProjectPath --configuration Release --no-build
if ($LASTEXITCODE -ne 0) {
    throw "AchievementSync functional checks failed with exit code $LASTEXITCODE. The existing output executable was not changed."
}

New-Item -ItemType Directory -Path $stagePath | Out-Null
$publishArguments = @(
    "publish", $projectPath,
    "--configuration", "Release",
    "--runtime", $Runtime,
    "--self-contained", "true",
    "--output", $stagePath,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:PublishReadyToRun=true",
    "-p:TieredCompilation=true",
    "-p:TieredPGO=true"
)
if (-not [string]::IsNullOrWhiteSpace($GitHubOAuthClientId)) {
    $publishArguments += "-p:AchievementSyncGitHubClientId=$GitHubOAuthClientId"
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE. The existing output executable was not changed. Staging files remain at '$stagePath'."
}
if (-not (Test-Path -LiteralPath $stageExe -PathType Leaf)) {
    throw "Publish completed without '$stageExe'. The existing output executable was not changed."
}

$stagedFile = Get-Item -LiteralPath $stageExe
if ($stagedFile.Length -lt 1MB) {
    throw "The staged executable is unexpectedly small ($($stagedFile.Length) bytes). The existing output executable was not changed."
}
$stagedHash = (Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash

try {
    if (Test-Path -LiteralPath $outputExe -PathType Leaf) {
        [System.IO.File]::Replace($stageExe, $outputExe, $backupExe, $true)
    }
    else {
        [System.IO.File]::Move($stageExe, $outputExe)
    }

    $installedHash = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash
    if ($installedHash -ne $stagedHash) {
        throw "The installed executable does not match the tested staged build."
    }

    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
catch {
    if (Test-Path -LiteralPath $backupExe -PathType Leaf) {
        [System.IO.File]::Replace($backupExe, $outputExe, $null, $true)
    }
    throw
}

Write-Output "Updated the single AchievementSync executable: $outputExe"
if ([string]::IsNullOrWhiteSpace($GitHubOAuthClientId)) {
    Write-Warning "ACHIEVEMENTSYNC_GITHUB_CLIENT_ID was empty; GitHub sign-in will remain unavailable in this build."
}
else {
    Write-Output "GitHub Device Flow is configured with the supplied public OAuth Client ID."
}
