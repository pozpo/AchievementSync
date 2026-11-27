<#
.SYNOPSIS
    Builds, smoke-tests and packages AchievementSync (default version 1.3.0).

.DESCRIPTION
    Run from the repository root on Windows x64 with the .NET 10 SDK installed:

        powershell -ExecutionPolicy Bypass -File .\build-release.ps1

    Steps: restore -> build -> smoke tests -> single-file publish -> verify the exe's product
    version -> zip the source tree together with AchievementSync.exe.

    Output: artifacts\AchievementSync-<Version>.zip
#>
[CmdletBinding()]
param(
    [string]$Version = '1.3.0',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Invoke-Step {
    param([string]$Title, [scriptblock]$Body)
    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $Title (exit code $LASTEXITCODE)" }
}

# 0. The project files must already carry the requested version.
$desktopProject = 'src\AchievementSync.Desktop\AchievementSync.Desktop.csproj'
$projectText = Get-Content -LiteralPath $desktopProject -Raw
if ($projectText -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "$desktopProject does not declare <Version>$Version</Version>."
}

$artifacts  = Join-Path $PSScriptRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$stageDir   = Join-Path $artifacts "AchievementSync-$Version"
$zipPath    = Join-Path $artifacts "AchievementSync-$Version.zip"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Remove-Item -LiteralPath $publishDir, $stageDir, $zipPath -Recurse -Force -ErrorAction SilentlyContinue

Invoke-Step 'Restore' { dotnet restore AchievementSync.sln }
Invoke-Step 'Build (Release)' { dotnet build AchievementSync.sln --configuration Release --no-restore }

if (-not $SkipTests) {
    Invoke-Step 'Smoke tests' {
        dotnet test tests/AchievementSync.SmokeTests/AchievementSync.SmokeTests.csproj --configuration Release
    }
}
else {
    Write-Warning 'Smoke tests skipped (-SkipTests).'
}

Invoke-Step "Publish single-file $Runtime executable" {
    dotnet publish $desktopProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir
}

$exe = Join-Path $publishDir 'AchievementSync.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish did not produce $exe" }

$info = (Get-Item -LiteralPath $exe).VersionInfo
Write-Host ("Executable version: product={0} file={1}" -f $info.ProductVersion, $info.FileVersion)
if (-not $info.ProductVersion.StartsWith($Version)) {
    throw "Executable product version '$($info.ProductVersion)' does not match $Version."
}

Write-Host ""
Write-Host '==> Package source + executable' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'source') | Out-Null
robocopy $PSScriptRoot (Join-Path $stageDir 'source') /E /XD bin obj artifacts output .git .vs /XF *.user /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
$global:LASTEXITCODE = 0
Copy-Item -LiteralPath $exe -Destination (Join-Path $stageDir 'AchievementSync.exe')
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Done: $zipPath" -ForegroundColor Green
Write-Host "SHA-256: $hash"
