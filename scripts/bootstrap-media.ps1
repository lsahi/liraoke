$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$toolRoot = Join-Path $repoRoot 'tools/ffmpeg'
$archivePath = Join-Path $repoRoot 'tools/ffmpeg-8.1.2.zip'
$downloadUrl = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip'
$expectedHash = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'
$binaryPath = Join-Path $toolRoot 'ffmpeg-8.1.2-essentials_build/bin/ffmpeg.exe'
if (Test-Path -LiteralPath $binaryPath) { Write-Output $binaryPath; exit 0 }
New-Item -ItemType Directory -Path (Join-Path $repoRoot 'tools') -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath
}
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'FFmpeg archive hash does not match the pinned build.' }
Expand-Archive -LiteralPath $archivePath -DestinationPath $toolRoot -Force
if (-not (Test-Path -LiteralPath $binaryPath)) { throw 'Expected FFmpeg executable was not found.' }
Write-Output $binaryPath
