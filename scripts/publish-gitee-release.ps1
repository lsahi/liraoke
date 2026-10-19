param(
    [Parameter(Mandatory)] [string] $Owner,
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $TargetCommitish,
    [Parameter(Mandatory)] [string[]] $Files,
    [Parameter(Mandatory)] [string] $AccessToken
)

$ErrorActionPreference = 'Stop'
$apiRoot = "https://gitee.com/api/v5/repos/$Owner/$Repository"
$headers = @{ Authorization = "Bearer $AccessToken" }
$release = $null

function New-GiteeVolumes {
    param([string] $ArchivePath, [long] $VolumeBytes = 90000000)
    if ($VolumeBytes -le 0 -or $VolumeBytes -ge 100000000) { throw 'Invalid volume size.' }
    $archive = Get-Item -LiteralPath $ArchivePath
    $directory = Join-Path $archive.DirectoryName ('gitee-volumes-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $parts = [Collections.Generic.List[string]]::new()
    $buffer = [byte[]]::new(1MB)
    $source = [IO.File]::OpenRead($archive.FullName)
    try {
        while ($source.Position -lt $source.Length) {
            $path = Join-Path $directory ('{0}.{1:D3}' -f $archive.Name, ($parts.Count + 1))
            $output = [IO.File]::Create($path)
            try {
                $remaining = $VolumeBytes
                while ($remaining -gt 0) {
                    $read = $source.Read($buffer, 0, [int][math]::Min($buffer.Length, $remaining))
                    if ($read -eq 0) { break }
                    $output.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            } finally { $output.Dispose() }
            $parts.Add($path)
        }
    } finally { $source.Dispose() }
    # Read the finished volumes in order to verify they reconstruct the original.
    $combined = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($part in $parts) {
            $stream = [IO.File]::OpenRead($part)
            try { while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $combined.AppendData($buffer, 0, $read) } }
            finally { $stream.Dispose() }
        }
        $reconstructedHash = [Convert]::ToHexString($combined.GetHashAndReset()).ToLowerInvariant()
    } finally { $combined.Dispose() }
    $originalHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($reconstructedHash -ne $originalHash) { throw 'Split ZIP verification failed.' }
    $checksums = Join-Path $directory ($archive.Name + '.parts.sha256')
    @($parts | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) }) |
        Set-Content -LiteralPath $checksums -Encoding ascii
    $first = [IO.Path]::GetFileName($parts[0])
    $names = @($parts | ForEach-Object { [IO.Path]::GetFileName($_) })
    $joinCommand = 'copy /b ' + (($names | ForEach-Object { '"' + $_ + '"' }) -join '+') + ' "' + $archive.Name + '"'
    $instructions = @"
Gitee 分卷下载与解压说明

请下载本版本全部分卷（共 $($parts.Count) 个）：
$($names -join "`n")

将全部分卷放在同一个文件夹，保持文件名不变。
安装 7-Zip (https://www.7-zip.org/)，用 7-Zip 打开 $first，然后解压。
不要分别解压后续分卷；Windows 内置 ZIP 解压可能不支持直接打开分卷。

也可在该文件夹打开 Windows 命令提示符 cmd（不是 PowerShell），执行：
$joinCommand
随后解压生成的 $($archive.Name)。如果目标 ZIP 已存在，请先移走它。

合并后的 ZIP 与 GitHub 完整 ZIP 字节一致。
原始 ZIP SHA256: $originalHash
*.parts.sha256 是各分卷校验值；*.zip.sha256 是合并后完整 ZIP 的校验值。
"@
    $readme = Join-Path $directory 'GITEE-DOWNLOAD-INSTRUCTIONS.txt'
    $instructions | Set-Content -LiteralPath $readme -Encoding utf8
    Write-Host "Gitee: verified $($parts.Count) volumes; reconstructed SHA256 $originalHash"
    return [pscustomobject]@{ Files=@($parts.ToArray()) + @($checksums, $readme); Instructions=$instructions }
}

$uploadFiles = [Collections.Generic.List[string]]::new()
$volumeInstructions = ''
foreach ($path in $Files) {
    $file = Get-Item -LiteralPath $path
    if ($file.Extension -eq '.zip' -and $file.Length -gt 90000000) {
        $volumes = New-GiteeVolumes -ArchivePath $file.FullName
        foreach ($volume in $volumes.Files) { $uploadFiles.Add($volume) }
        $volumeInstructions = $volumes.Instructions
    } else {
        if ($file.Length -ge 100000000) { throw "Attachment too large: $($file.Name)" }
        $uploadFiles.Add($file.FullName)
    }
}

# Keep the default request timeouts while logging where publishing waits.
# Do not retry writes automatically: a timed-out upload may have reached Gitee.
function Invoke-GiteeRequest {
    param($Method, $Uri, $Headers, $ContentType, $Body, $Form)
    $request = @{} + $PSBoundParameters
    $request.StatusCodeVariable = 'httpStatus'
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "Gitee: starting $Method $Uri"
    try {
        $result = Invoke-RestMethod @request
        Write-Host "Gitee: completed $Method HTTP $httpStatus after $([math]::Round($timer.Elapsed.TotalSeconds, 1))s"
        return $result
    }
    catch {
        $status = if ($null -ne $_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unavailable' }
        Write-Host "Gitee: failed $Method $Uri after $([math]::Round($timer.Elapsed.TotalSeconds, 1))s; HTTP=$status; exception=$($_.Exception.GetType().Name)"
        throw
    }
}

try {
    $release = Invoke-GiteeRequest `
        -Method Get `
        -Uri "$apiRoot/releases/tags/$([uri]::EscapeDataString($Tag))" `
        -Headers $headers
}
catch {
    $statusCode = [int]$_.Exception.Response.StatusCode
    if ($statusCode -ne 404) { throw }
}

if ($null -eq $release -or $null -eq $release.id) {
    $isPrerelease = $Tag.Contains('-')
    $release = Invoke-GiteeRequest `
        -Method Post `
        -Uri "$apiRoot/releases" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body (@{
            tag_name = $Tag
            target_commitish = $TargetCommitish
            name = "Liraoke $Tag"
            body = "Automated Windows x64 portable release for $Tag."
            prerelease = $isPrerelease
        } | ConvertTo-Json)
}

if ($null -eq $release -or $null -eq $release.id) {
    throw "Gitee did not return a valid Release object after creating tag $Tag."
}

$attachmentsUri = "$apiRoot/releases/$($release.id)/attach_files"
if ($volumeInstructions) {
    $body = [string]$release.body
    $body = $body -replace '(?s)\s*<!-- liraoke-volumes:start -->.*?<!-- liraoke-volumes:end -->', ''
    $body += "`n`n<!-- liraoke-volumes:start -->`n$volumeInstructions`n<!-- liraoke-volumes:end -->"
    Invoke-GiteeRequest -Method Patch -Uri "$apiRoot/releases/$($release.id)" -Headers $headers `
        -ContentType 'application/json' -Body (@{body=$body} | ConvertTo-Json) | Out-Null
}
$existingAttachments = @(
    Invoke-GiteeRequest `
        -Method Get `
        -Uri "${attachmentsUri}?page=1&per_page=100&direction=asc" `
        -Headers $headers
)

foreach ($filePath in $uploadFiles) {
    $file = Get-Item -LiteralPath $filePath
    Write-Host "Gitee: preparing $($file.Name), $($file.Length) bytes"
    $existing = $existingAttachments | Where-Object { $_.name -eq $file.Name }
    foreach ($attachment in $existing) {
        Invoke-GiteeRequest `
            -Method Delete `
            -Uri "$attachmentsUri/$($attachment.id)" `
            -Headers $headers | Out-Null
    }

    Invoke-GiteeRequest `
        -Method Post `
        -Uri $attachmentsUri `
        -Headers $headers `
        -Form @{ file = $file } | Out-Null
}

$verified = @(Invoke-GiteeRequest -Method Get -Uri "${attachmentsUri}?page=1&per_page=100" -Headers $headers)
foreach ($path in $uploadFiles) {
    $file = Get-Item -LiteralPath $path
    if (-not ($verified | Where-Object { $_.name -eq $file.Name -and $_.size -eq $file.Length })) {
        throw "Gitee attachment verification failed: $($file.Name)"
    }
}
Write-Output "Published $Tag to Gitee with $($uploadFiles.Count) verified attachment(s)."
