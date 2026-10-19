# Build/release prerequisite only. End users receive the assembled CPU runtime.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot 'tools/demucs'
if (Test-Path -LiteralPath (Join-Path $output 'cpu/runtime.json')) { Write-Output 'CPU runtime already prepared.'; return }
if (Test-Path -LiteralPath $output) { throw 'An incomplete tools/demucs directory exists. Inspect it before retrying; it will not be overwritten.' }
$buildRoot = Join-Path $repoRoot 'tools/demucs-build'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

function Get-VerifiedBuildFile([string]$Url, [string]$Path, [string]$Hash) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash) { return }
    $partial = "$Path.partial"
    Invoke-WebRequest -Uri $Url -OutFile $partial -TimeoutSec 1800
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) { throw "SHA-256 mismatch: $Url" }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

$pythonArchive = Join-Path $buildRoot 'cpython-3.12.15-win-x64.tar.gz'
Get-VerifiedBuildFile 'https://github.com/astral-sh/python-build-standalone/releases/download/20261013/cpython-3.12.15%2B20261013-x86_64-pc-windows-msvc-install_only_stripped.tar.gz' $pythonArchive '7a2aef48e5ee569e310b29b32681debbc5d888e92790d85331a47736c6b06aad'
$pythonRoot = Join-Path $buildRoot 'python'
$python = Join-Path $pythonRoot 'python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    & tar -xzf $pythonArchive -C $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Python extraction failed.' }
}
$venv = Join-Path $buildRoot 'venv'
$venvPython = Join-Path $venv 'Scripts/python.exe'
if (-not (Test-Path -LiteralPath $venvPython)) {
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'CPU build environment creation failed.' }
}
$wheel = Join-Path $buildRoot 'torch-2.8.0+cpu-cp312-cp312-win_amd64.whl'
Get-VerifiedBuildFile 'https://download.pytorch.org/whl/cpu/torch-2.8.0%2Bcpu-cp312-cp312-win_amd64.whl' $wheel '2be20b2c05a0cce10430cc25f32b689259640d273232b2de357c35729132256d'
& $venvPython -m pip install --disable-pip-version-check --no-deps $wheel
if ($LASTEXITCODE -ne 0) { throw 'CPU PyTorch installation failed.' }
& $venvPython -m pip install --disable-pip-version-check --no-deps --index-url https://pypi.org/simple -r (Join-Path $PSScriptRoot 'demucs-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Pinned Demucs dependency installation failed.' }
& $venvPython -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Demucs dependency verification failed.' }
$model = Join-Path $buildRoot '955717e8.safetensors'
Get-VerifiedBuildFile 'https://huggingface.co/adefossez/HTDemucs/resolve/cbc8a9b1a87023b7fd74e7b3412e6321c0eab003/955717e8.safetensors' $model 'd9fa14133cfcc034a6758923bb3a8ca9f8dfd0b582134643bbf83f72c17576dd'
& $python (Join-Path $PSScriptRoot 'prepare-demucs-runtime.py') --python-root $pythonRoot --site-packages (Join-Path $venv 'Lib/site-packages') --model $model --output $output
if ($LASTEXITCODE -ne 0) { throw 'CPU runtime assembly/self-test failed.' }
