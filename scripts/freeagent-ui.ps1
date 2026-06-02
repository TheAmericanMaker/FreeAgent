# Launch the FreeAgent TUI (Windows). Thin wrapper so you don't have to remember the cd + bun dance.
# Run setup first with scripts\install-tui.ps1. Any args are forwarded to the TUI.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tuiDir = Join-Path $repoRoot 'clients\tui'

$bun = (Get-Command bun -ErrorAction SilentlyContinue).Source
if (-not $bun) { $bun = Join-Path $env:USERPROFILE '.bun\bin\bun.exe' }
if (-not (Test-Path $bun)) {
  Write-Error "Bun not found. Run scripts\install-tui.ps1 first."
  exit 1
}

Push-Location $tuiDir
try { & $bun run src/tui.tsx @args } finally { Pop-Location }
