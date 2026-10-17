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

# PowerShell 7.4+ separates connection and response-stream read timeouts.
# Do not retry writes automatically: a timed-out upload may have reached Gitee.
function Invoke-GiteeRequest {
    param($Method, $Uri, $Headers, $ContentType, $Body, $Form)
    $request = @{} + $PSBoundParameters
    $request.ConnectionTimeoutSeconds = 30
    $request.OperationTimeoutSeconds = 120
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
$existingAttachments = @(
    Invoke-GiteeRequest `
        -Method Get `
        -Uri "${attachmentsUri}?page=1&per_page=100&direction=asc" `
        -Headers $headers
)

foreach ($filePath in $Files) {
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

Write-Output "Published $Tag to Gitee with $($Files.Count) attachment(s)."
