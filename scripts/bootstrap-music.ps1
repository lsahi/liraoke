param([switch]$SkipNodeBootstrap)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $SkipNodeBootstrap) { & (Join-Path $PSScriptRoot 'bootstrap-ai.ps1') }
$distribution = Join-Path $repo 'tools/ai-download/node-v22.23.4-win-x64'
$node = Join-Path $distribution 'node.exe'
$npm = Join-Path $distribution 'node_modules/npm/bin/npm-cli.js'
Push-Location (Join-Path $repo 'src/Liraoke.MusicHost')
try {
    & $node $npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Music dependencies failed.' }
    & $node build.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Music host build failed.' }
    & $node --test 'test/*.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Music host tests failed.' }
} finally { Pop-Location }
