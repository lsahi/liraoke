$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$cpuMarker = Join-Path $repoRoot 'tools/demucs/cpu/runtime.json'
if (-not (Test-Path -LiteralPath $cpuMarker)) {
    & (Join-Path $PSScriptRoot 'bootstrap-demucs.ps1')
    if (-not (Test-Path -LiteralPath $cpuMarker)) { throw 'CPU separation runtime is incomplete; publishing refused.' }
}
$project = Join-Path $repoRoot 'src/KaraokeFilmMaker.App/KaraokeFilmMaker.App.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project)
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$packageName = "Liraoke-$version-win-x64"
$output = Join-Path $repoRoot "artifacts/package/$packageName"
$config = Join-Path $repoRoot 'NuGet.Config'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:RestoreConfigFile=$config -o $output
if ($LASTEXITCODE -ne 0) { throw 'MVP publishing failed.' }
$archive = Join-Path $repoRoot "artifacts/package/$packageName.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
Write-Output $archive
