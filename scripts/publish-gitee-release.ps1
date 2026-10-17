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

try {
    $release = Invoke-RestMethod `
        -Method Get `
        -Uri "$apiRoot/releases/tags/$([uri]::EscapeDataString($Tag))" `
        -Headers $headers
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }

    $isPrerelease = $Tag.Contains('-')
    $release = Invoke-RestMethod `
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
    throw "Gitee did not return a valid Release object for tag $Tag."
}

$attachmentsUri = "$apiRoot/releases/$($release.id)/attach_files"

foreach ($filePath in $Files) {
    $file = Get-Item -LiteralPath $filePath
    Invoke-RestMethod `
        -Method Post `
        -Uri $attachmentsUri `
        -Headers $headers `
        -Form @{ file = $file } | Out-Null
}

Write-Output "Published $Tag to Gitee with $($Files.Count) attachment(s)."
