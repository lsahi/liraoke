#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Archive')]
param(
    [Parameter(Mandatory)] [ValidatePattern('^v[0-9][A-Za-z0-9._-]*$')] [string] $Tag,
    [Parameter(Mandatory, ParameterSetName = 'Archive')] [string] $ArchivePath,
    [Parameter(Mandatory, ParameterSetName = 'Build')] [switch] $Build,
    [string] $Owner = 'harubilira',
    [string] $Repository = 'liraoke'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($Build) {
    Push-Location $repoRoot
    try {
        $tagCommit = git rev-parse --verify "refs/tags/${Tag}^{commit}"
        if ($LASTEXITCODE -ne 0) { throw 'Fetch the requested tag first.' }
        $headCommit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $headCommit -ne $tagCommit) { throw 'Build requires HEAD at the requested tag. Use -ArchivePath for historical release ZIPs.' }
        $changes = git status --porcelain
        if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Build requires a clean working tree.' }
        $project = [xml](Get-Content 'src/KaraokeFilmMaker.App/KaraokeFilmMaker.App.csproj')
        $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
        if (($Tag -replace '^v', '' -replace '-.*$', '') -ne $version) { throw 'Tag and project version do not match.' }
        & "$PSScriptRoot/bootstrap-media.ps1"
        & "$PSScriptRoot/publish-mvp.ps1"
        $ArchivePath = Join-Path $repoRoot "artifacts/package/Liraoke-$version-win-x64.zip"
    }
    finally { Pop-Location }
}
$archive = Get-Item -LiteralPath $ArchivePath
if ($archive.PSIsContainer -or $archive.Extension -ne '.zip' -or $archive.Length -eq 0) { throw 'Select a nonempty portable release ZIP.' }
$checksumPath = "$($archive.FullName).sha256"
$hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath $checksumPath) {
    $expected = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
    if ($expected -ne $hash) { throw 'Existing SHA256 file does not match the ZIP.' }
} else {
    "$hash  $($archive.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii
}
Write-Host "Upload $($archive.FullName) ($($archive.Length) bytes) to $Owner/$Repository, tag $Tag"
Write-Host "SHA256: $hash"
Write-Host 'The tag must already exist on Gitee. Same-named attachments will be replaced.'
$token = $env:GITEE_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) { $token = Read-Host 'Gitee PAT (masked)' -MaskInput }
if ([string]::IsNullOrWhiteSpace($token)) { throw 'A Gitee PAT is required.' }
try {
    & "$PSScriptRoot/publish-gitee-release.ps1" -Owner $Owner -Repository $Repository -Tag $Tag -TargetCommitish $Tag -Files @($archive.FullName, $checksumPath) -AccessToken $token
}
finally { $token = $null }
