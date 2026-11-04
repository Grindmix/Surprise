[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Rid = 'win-x64',
    [switch]$Offline,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$distribution = Join-Path $artifactRoot "release/$Rid"
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'fetch-mpv.ps1') -Rid $Rid -Offline:$Offline
    if (-not $SkipTests) {
        foreach ($testProject in @('tests/Surprise.Tests', 'tests/Surprise.AnimationTests')) {
            & dotnet run --project $testProject -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
        }
    }
    $publishDirectory = Join-Path $artifactRoot "publish/$Rid/App"
    $publishArgs = @('publish', 'src/Surprise.App/Surprise.App.csproj', '-c', 'Release', '-r', $Rid,
        '--self-contained', 'true', '-m:1', '-nr:false', '-o', $publishDirectory,
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugType=embedded')
    if ($Offline) { $publishArgs += @('--ignore-failed-sources', '-p:NuGetAudit=false') }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publishing Surprise failed.' }
    New-Item -ItemType Directory -Path $distribution -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'Surprise.exe') -Destination $distribution -Force
    foreach ($document in @('README.md', 'IMPLEMENTATION_PLAN.md', 'THIRD-PARTY.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $distribution -Force }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party') -Destination $distribution -Recurse -Force
    # Remove only named obsolete release binaries, never user settings/media.
    foreach ($oldName in @('Surprise.Setup.exe', 'Surprise.Executor.exe')) {
        $oldFile = [IO.Path]::GetFullPath((Join-Path $distribution $oldName))
        if (-not $oldFile.StartsWith([IO.Path]::GetFullPath($artifactRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid artifact path.' }
        if (Test-Path -LiteralPath $oldFile) { Remove-Item -LiteralPath $oldFile -Force }
    }
    $report = Join-Path $artifactRoot "backend-check-$Rid.json"
    $check = Start-Process -FilePath (Join-Path $distribution 'Surprise.exe') -ArgumentList @('--check-backend', ('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($check.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report) -or -not (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).Ok) { throw 'Bundled player check failed.' }
    Get-FileHash -LiteralPath (Join-Path $distribution 'Surprise.exe') -Algorithm SHA256 | Format-Table -AutoSize
    Write-Host "Ready: $(Join-Path $distribution 'Surprise.exe')"
} finally { Pop-Location }
