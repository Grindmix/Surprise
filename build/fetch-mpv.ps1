[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Rid = 'win-x64', [switch]$Offline)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$package = if ($arch -eq 'arm64') { 'mpv-aarch64-20261004-git-413ff0b1cd.7z' } else { 'mpv-x86_64-20261004-git-413ff0b1cd.7z' }
$expected = if ($arch -eq 'arm64') { '68FC5157D6C947B10E3A6DBD2C729937F508C9914C64BA34D9457B70D3CC57F5' } else { '0703A0D62C60B2C68511C6A101DB82A31C32C69BCDD86941632A1E76BB1699B7' }
$vendor = Join-Path $projectRoot "artifacts/vendor/mpv/$arch"
New-Item -ItemType Directory -Path $vendor -Force | Out-Null
$archive = Join-Path $vendor $package
$binary = Join-Path $vendor 'mpv.exe'
$checksum = Join-Path $vendor 'mpv.sha256'
if ((Test-Path -LiteralPath $binary) -and (Test-Path -LiteralPath $checksum)) {
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne (Get-Content -LiteralPath $checksum -Raw).Trim()) { throw 'Cached mpv binary checksum mismatch.' }
    Write-Host "Verified cached mpv ($arch)."; return
}
if (-not (Test-Path -LiteralPath $archive)) {
    $previous = Join-Path $projectRoot "artifacts/vendor/mpv/mpv-$arch.7z"
    if (Test-Path -LiteralPath $previous) { Copy-Item -LiteralPath $previous -Destination $archive }
    elseif ($Offline) { throw 'mpv is missing. Run build/fetch-mpv.ps1 online first.' }
    else { Invoke-WebRequest "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261004/$package" -OutFile $archive }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Downloaded mpv archive checksum mismatch.' }
& tar.exe -xf $archive -C $vendor mpv.exe
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $binary)) { throw 'Could not extract mpv with Windows tar.exe.' }
(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash | Set-Content -LiteralPath $checksum -Encoding ascii
Write-Host "Prepared pinned mpv ($arch)."
