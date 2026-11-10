$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$download = Join-Path $repo 'tools/ai-download'
$runtime = Join-Path $repo 'tools/ai-runtime'
$version = '22.23.4'
$sha256 = 'cbc74b5e0982c9dbb41525fd8fb5dcd32fbee237863f7829072f4639a1ce5f20'
$archive = Join-Path $download 'node.zip'
$distribution = Join-Path $download "node-v$version-win-x64"
New-Item -ItemType Directory -Force $download, $runtime | Out-Null
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $sha256) {
    Invoke-WebRequest "https://nodejs.org/dist/v$version/node-v$version-win-x64.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $sha256) { throw 'Node archive SHA256 mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $download -Force
$node = Join-Path $distribution 'node.exe'
$npm = Join-Path $distribution 'node_modules/npm/bin/npm-cli.js'
Push-Location (Join-Path $repo 'src/Liraoke.AiHost')
try {
    & $node $npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'AI dependency restore failed.' }
    & $node build.mjs
    if ($LASTEXITCODE -ne 0) { throw 'AI host build failed.' }
    & $node --test 'test/*.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'AI offline tests failed.' }
} finally { Pop-Location }
Copy-Item -LiteralPath $node -Destination (Join-Path $runtime 'node.exe') -Force
Copy-Item -LiteralPath (Join-Path $distribution 'LICENSE') -Destination (Join-Path $runtime 'NODE-LICENSE.txt') -Force
Write-Output "AI runtime ready: $runtime"
