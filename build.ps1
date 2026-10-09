# Build script for DataManager
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1
# Output goes to .\dist (framework-dependent, ~0.4 MB)
#
# NOTE: keep this file ASCII-only on purpose.
# PowerShell 5.1 reads BOM-less .ps1 files as ANSI, which mangles non-ASCII
# text and can merge lines, silently breaking logic below. ASCII avoids that
# whole class of bug regardless of how the file is saved.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'DataManager.csproj'
$out  = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'need dotnet SDK 9.0'
    exit 1
}

# dist may also hold runtime data (Data\data.sqlite, Backup\, config.json).
# If present, leave it alone; only program files are cleared here.
# Note these are optional -- a fresh dist legitimately has none of them,
# so remember what existed and verify afterwards that it survived.
$runtimeData = @('Data', 'Backup', 'config.json')

$presentBefore = @()
foreach ($item in $runtimeData) {
    if (Test-Path (Join-Path $out $item)) {
        $presentBefore += $item
    }
}

if (Test-Path $out) {
    $stale = Get-ChildItem -Force $out | Where-Object { $runtimeData -notcontains $_.Name }
    foreach ($item in $stale) {
        Remove-Item $item.FullName -Recurse -Force
    }
}

& dotnet publish $proj `
    -c Release `
    -f net9.0-windows `
    --self-contained false `
    -p:PublishSingleFile=false `
    -o $out `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $out 'DataManager.exe'
if (-not (Test-Path $exe)) {
    Write-Error 'DataManager.exe'
    exit 1
}

# Post-flight check: anything that existed before publishing must still exist.
# Items that were absent before are expected to stay absent, so deleting them
# yourself does not trip this check.
foreach ($item in $presentBefore) {
    $path = Join-Path $out $item
    if (-not (Test-Path $path)) {
        Write-Error "publish removed runtime data: $path"
        exit 1
    }
}

Write-Host $exe
