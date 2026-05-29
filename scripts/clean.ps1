param(
  [switch]$IncludeNodeModules = $false,
  [switch]$Quiet = $false
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$paths = @(
  'dist',
  'dist-ssr',
  'src-tauri/target',
  'src-tauri/gen/schemas',
  '.mypy_cache',
  '.venv',
  '__pycache__',
  'tmp',
  'artifacts'
)

if ($IncludeNodeModules) {
  $paths += 'node_modules'
}

foreach ($relative in $paths) {
  $full = Join-Path $root $relative
  if (Test-Path $full) {
    try {
      Remove-Item -Path $full -Recurse -Force
      if (-not $Quiet) { Write-Host "[OK] Removed: $relative" }
    }
    catch {
      Write-Warning "[SKIP] Failed to remove $relative : $($_.Exception.Message)"
    }
  }
  elseif (-not $Quiet) {
    Write-Host "[SKIP] Not found: $relative"
  }
}

if (-not $Quiet) {
  Write-Host "Clean completed."
  Write-Host "Tip: use -IncludeNodeModules to remove node_modules as well."
}
