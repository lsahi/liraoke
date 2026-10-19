#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory)] [ValidatePattern('^v[0-9][A-Za-z0-9._-]*$')] [string] $Tag,
    [Parameter(ParameterSetName = 'Build')] [switch] $Build,
    [Parameter(Mandatory, ParameterSetName = 'Archive')] [string] $ArchivePath,
    [ValidateSet('Both','GitHub','Gitee')] [string] $Platform = 'Both'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($PSCmdlet.ParameterSetName -eq 'Build') {
    # Export the exact tag into an isolated directory; leave the checkout intact.
    $commit = git -C $root rev-parse --verify "refs/tags/${Tag}^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "Fetch tag $Tag before building." }
    $buildRoot = Join-Path $root "artifacts/local-release/$Tag-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    $sourceArchive = "$buildRoot.tar"
    git -C $root archive --format=tar -o $sourceArchive $commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not export release sources.' }
    # Git for Windows puts GNU tar on PATH; a drive-letter path is interpreted
    # as a remote archive by GNU tar. Relative paths work with both tar variants.
    Push-Location $root
    try {
        $relativeArchive = [IO.Path]::GetRelativePath($root, $sourceArchive).Replace('\', '/')
        $relativeBuild = [IO.Path]::GetRelativePath($root, $buildRoot).Replace('\', '/')
        tar -xf $relativeArchive -C $relativeBuild
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract release sources.' }
    } finally { Pop-Location }
    $project = [xml](Get-Content -LiteralPath "$buildRoot/src/KaraokeFilmMaker.App/KaraokeFilmMaker.App.csproj")
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if (($Tag -replace '^v', '' -replace '-.*$', '') -ne $version) { throw 'Tag and project version do not match.' }
    Write-Host "Building $Tag ($commit) in $buildRoot"
    $localManifest = Join-Path $root 'scripts/media-toolchain.json'
    $tagManifest = Join-Path $buildRoot 'scripts/media-toolchain.json'
    if ((Test-Path $localManifest) -and (Test-Path $tagManifest) -and
        ((Get-Content $localManifest -Raw | ConvertFrom-Json).sha256 -eq
         (Get-Content $tagManifest -Raw | ConvertFrom-Json).sha256) -and
        (Test-Path "$root/tools/ffmpeg/ffmpeg-8.1.2-essentials_build/bin/ffmpeg.exe")) {
        Write-Host 'Reusing installed FFmpeg with matching toolchain manifest.'
        New-Item -ItemType Directory -Path "$buildRoot/tools" -Force | Out-Null
        Copy-Item -LiteralPath "$root/tools/ffmpeg" -Destination "$buildRoot/tools/ffmpeg" -Recurse
    }
    Push-Location $buildRoot
    try {
        & "$buildRoot/scripts/bootstrap-media.ps1"
        & "$buildRoot/scripts/publish-mvp.ps1"
    } finally { Pop-Location }
    $ArchivePath = "$buildRoot/artifacts/package/Liraoke-$version-win-x64.zip"
}
$archive = Get-Item -LiteralPath $ArchivePath
if ($archive.PSIsContainer -or $archive.Extension -ne '.zip' -or $archive.Length -eq 0) { throw 'Select a nonempty portable ZIP.' }
$hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$($archive.FullName).sha256"
if (Test-Path -LiteralPath $checksum) {
    if (((Get-Content -LiteralPath $checksum -Raw).Trim() -split '\s+')[0] -ne $hash) { throw 'SHA256 mismatch.' }
} else { "$hash  $($archive.Name)" | Set-Content -LiteralPath $checksum -Encoding ascii }
Write-Host "Package: $($archive.FullName)"
Write-Host "SHA256: $hash"
Write-Host 'Remote tags must already exist. Same-named release attachments will be replaced.'

if ($Platform -in @('Both','GitHub')) {
    $token = $env:GH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) { $token = Read-Host 'GitHub PAT (masked)' -MaskInput }
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'GitHub PAT required.' }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'Liraoke-local-release'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $api = 'https://api.github.com/repos/lsahi/liraoke'
    try {
        # Fail before publishing if the tag is absent; do not create it from master.
        Invoke-RestMethod -Uri "$api/git/ref/tags/$Tag" -Headers $headers | Out-Null
        $release = $null
        try { $release = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $headers }
        catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
        if ($null -eq $release) {
            $body = @{ tag_name=$Tag; name="Liraoke $Tag"; prerelease=$Tag.Contains('-'); body="Windows x64 portable release for $Tag." } | ConvertTo-Json
            $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -Body $body
        }
        $uploadUrl = $release.upload_url -replace '\{.*$', ''
        if (-not $release.id -or ([uri]$uploadUrl).Host -ne 'uploads.github.com') { throw 'Invalid GitHub release response.' }
        foreach ($path in @($archive.FullName,$checksum)) {
            $file = Get-Item -LiteralPath $path
            foreach ($asset in @($release.assets | Where-Object name -EQ $file.Name)) {
                Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($asset.id)" -Headers $headers | Out-Null
            }
            Write-Host "GitHub: uploading $($file.Name) ($($file.Length) bytes)"
            $uploaded = Invoke-RestMethod -Method Post -Uri "${uploadUrl}?name=$([uri]::EscapeDataString($file.Name))" -Headers $headers -ContentType 'application/octet-stream' -InFile $file.FullName
            if ($uploaded.state -ne 'uploaded' -or $uploaded.size -ne $file.Length) { throw 'GitHub asset verification failed.' }
            Write-Host "GitHub: uploaded $($uploaded.browser_download_url)"
        }
    } finally { $token = $null; $headers = $null }
}
if ($Platform -in @('Both','Gitee')) {
    & "$PSScriptRoot/publish-gitee-local.ps1" -Tag $Tag -ArchivePath $archive.FullName
}
Write-Host "Finished publishing to $Platform. Retain the ZIP and checksum for any retry."
