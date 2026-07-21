# FreeAgent TUI installer (Windows / PowerShell).
#
#   powershell -ExecutionPolicy Bypass -File scripts\install-tui.ps1
#
# Sets up the full-screen TUI app: installs Bun if missing, restores the TUI's deps, and publishes
# FreeAgent.Server as a self-contained binary into clients\tui\dist\server so the TUI launches it
# instantly with no .NET SDK at run time.
# After this, run the app with:   scripts\freeagent-ui.ps1   (or: cd clients\tui ; bun run tui)
#
# (This installs the graphical TUI. For the headless CLI global tool instead, see scripts\install.sh.)
#
# Flags:
#   -SkipPublish   only set up the TUI (use the dev `dotnet run` server path)
#   -Runtime <rid> override the publish RID (default: auto win-x64 / win-arm64)

param(
  [switch]$SkipPublish,
  [string]$Runtime
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tuiDir = Join-Path $repoRoot 'clients\tui'
$distServer = Join-Path $tuiDir 'dist\server'

function Say($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg) { Write-Host "  + $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  ! $msg" -ForegroundColor Yellow }

Say 'FreeAgent setup (Windows)'

# 1. Bun ------------------------------------------------------------------------------------------
$bun = (Get-Command bun -ErrorAction SilentlyContinue)
if (-not $bun) {
  $bunExe = Join-Path $env:USERPROFILE '.bun\bin\bun.exe'
  if (Test-Path $bunExe) {
    $bun = @{ Source = $bunExe }
  } else {
    Say 'Installing Bun…'
    Invoke-RestMethod https://bun.sh/install.ps1 | Invoke-Expression
    $bunExe = Join-Path $env:USERPROFILE '.bun\bin\bun.exe'
    if (-not (Test-Path $bunExe)) { throw 'Bun install did not produce bun.exe.' }
    $bun = @{ Source = $bunExe }
  }
}
$bunPath = $bun.Source
Ok "Bun: $bunPath"

# 2. TUI dependencies -----------------------------------------------------------------------------
Say 'Installing TUI dependencies…'
Push-Location $tuiDir
try { & $bunPath install } finally { Pop-Location }
Ok 'bun install complete'

# 3. Publish the server ---------------------------------------------------------------------------
if ($SkipPublish) {
  Warn 'Skipping server publish (-SkipPublish). The TUI will use `dotnet run` (slower; needs the .NET SDK).'
} else {
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (`dotnet`) is required to publish the server. Install .NET 10 SDK, or re-run with -SkipPublish.'
  }
  if (-not $Runtime) {
    $Runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
  }
  Say "Publishing FreeAgent.Server ($Runtime, self-contained)…"
  if (Test-Path $distServer) { Remove-Item -Recurse -Force $distServer }
  # Single-file + compression keeps the download small; no trimming (the kernel uses reflection to
  # discover capability types, which trimming would strip).
  & dotnet publish (Join-Path $repoRoot 'src\FreeAgent.Server') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true `
    -o $distServer
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
  Ok "Published to $distServer"
}

Write-Host ''
Say 'Done.'
Write-Host '  Run the app with:' -ForegroundColor White
Write-Host '    scripts\freeagent-ui.ps1' -ForegroundColor White
Write-Host '  or:' -ForegroundColor White
Write-Host '    cd clients\tui ; bun run tui' -ForegroundColor White
