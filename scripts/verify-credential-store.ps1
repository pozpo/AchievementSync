$ErrorActionPreference = "Stop"
$desktopAssemblyPath = Join-Path $PSScriptRoot "..\src\AchievementSync.Desktop\bin\Release\net10.0-windows\AchievementSync.dll"
if (-not (Test-Path -LiteralPath $desktopAssemblyPath -PathType Leaf)) {
    throw "Build the Release solution before running this check: $desktopAssemblyPath"
}

$assembly = [System.Reflection.Assembly]::LoadFrom([System.IO.Path]::GetFullPath($desktopAssemblyPath))
$storeType = $assembly.GetType("AchievementSync.Desktop.Services.WindowsCredentialTokenStore", $true)
$target = "AchievementSync/Verification/" + [Guid]::NewGuid().ToString("N")
$store = [System.Activator]::CreateInstance($storeType, [object[]]@($target))
$syntheticToken = "synthetic-credential-verification"
$writeMethod = $storeType.GetMethod("Write")
$readMethod = $storeType.GetMethod("Read")
$deleteMethod = $storeType.GetMethod("Delete")

try {
    $writeMethod.Invoke($store, [object[]]@($syntheticToken)) | Out-Null
    $stored = $readMethod.Invoke($store, @())
    if ($stored -cne $syntheticToken) {
        throw "Windows Credential Manager did not return the synthetic verification value."
    }
    Write-Output "Windows Credential Manager write/read succeeded for an isolated synthetic test target."
}
finally {
    $deleteMethod.Invoke($store, @()) | Out-Null
}
