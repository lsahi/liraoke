#Requires -Version 7.4
param([string] $RemoteName, [string] $RemoteUrl)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$updates = @()
while ($null -ne ($line = [Console]::ReadLine())) { $updates += $line }
$target = switch ($RemoteUrl) {
    'git@github.com:lsahi/liraoke.git' { 'GitHub' }
    'https://github.com/lsahi/liraoke.git' { 'GitHub' }
    'git@gitee.com:harubilira/liraoke.git' { 'Gitee' }
    'https://gitee.com/harubilira/liraoke.git' { 'Gitee' }
    default { $null }
}
foreach ($update in $updates) {
    $fields = $update -split '\s+'
    if ($fields.Count -ne 4 -or $fields[2] -ne 'refs/heads/master' -or $fields[1] -match '^0+$') { continue }
    if (-not $target) { throw "Unrecognized master push destination: $RemoteUrl" }
    $config = Get-Content -LiteralPath "$root/.local-release/config.json" -Raw | ConvertFrom-Json
    if (-not $config.enabled) { continue }
    $token = if ($target -eq 'GitHub') { $config.githubPat } else { $config.giteePat }
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Fill the $target PAT in .local-release/config.json before pushing master." }
    $sha = $fields[1]
    $xmlText = git -C $root show "${sha}:src/KaraokeFilmMaker.App/KaraokeFilmMaker.App.csproj"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read pushed project version.' }
    $project = [xml](($xmlText -join "`n").TrimStart([char]0xFEFF))
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+(\.\d+){2,3}$') { throw "Unsupported project version: $version" }
    $tag = "v$version-local.$($sha.Substring(0,12))"
    $existing = git -C $root rev-parse --verify --quiet "refs/tags/${tag}^{commit}"
    if ($LASTEXITCODE -eq 0) {
        if ($existing -ne $sha) { throw "Tag $tag points to a different commit." }
    } else {
        git -C $root tag $tag $sha
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create local release tag.' }
    }
    @{ version=$version; tag=$tag; commit=$sha; platform=$target } | ConvertTo-Json |
        Set-Content -LiteralPath "$root/.local-release/last-release.json" -Encoding utf8
    # Recursive pre-push receives only a tag and exits without publishing.
    git -C $root push $RemoteUrl "refs/tags/${tag}:refs/tags/${tag}"
    if ($LASTEXITCODE -ne 0) { throw 'Release tag push failed; master push aborted.' }
    $previousGithub = $env:GH_TOKEN
    $previousGitee = $env:GITEE_TOKEN
    try {
        if ($target -eq 'GitHub') { $env:GH_TOKEN = $token } else { $env:GITEE_TOKEN = $token }
        $cachePath = "$root/.local-release/package-$sha.txt"
        if (Test-Path -LiteralPath $cachePath) {
            $archive = (Get-Content -LiteralPath $cachePath -Raw).Trim()
            & "$PSScriptRoot/publish-local.ps1" -Tag $tag -ArchivePath $archive -Platform $target
        } else {
            & "$PSScriptRoot/publish-local.ps1" -Tag $tag -Build -Platform $target
            $archive = Get-ChildItem -Path "$root/artifacts/local-release/$tag-*/artifacts/package/Liraoke-$version-win-x64.zip" |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if (-not $archive) { throw 'Published ZIP was not found for reuse.' }
            $archive.FullName | Set-Content -LiteralPath $cachePath -Encoding utf8
        }
    } finally {
        $env:GH_TOKEN = $previousGithub
        $env:GITEE_TOKEN = $previousGitee
        $token = $null
    }
}
