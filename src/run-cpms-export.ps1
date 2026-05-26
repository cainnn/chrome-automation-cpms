# CPMS export + DB import launcher
# Usage: .\run-cpms-export.ps1
# Unattended: $env:CPMS_UNATTENDED="1"; .\run-cpms-export.ps1

$ErrorActionPreference = "Stop"

try {
    chcp 65001 | Out-Null
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
} catch { }

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root
$Unattended = $env:CPMS_UNATTENDED -eq "1"

Write-Host "=== CPMS Export + Database Import ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Daily workflow:" -ForegroundColor Yellow
Write-Host "  1. Open Chrome normally and log in to CPMS"
Write-Host "  2. Refresh extension at chrome://extensions/ (after updates)"
Write-Host "  3. Run this script - Chrome stays open, tabs are reused"
Write-Host ""
Write-Host "Note: This script never closes Chrome." -ForegroundColor Gray
Write-Host ""

$portInUse = Get-NetTCPConnection -LocalPort 9333 -State Listen -ErrorAction SilentlyContinue
if (-not $portInUse) {
    Write-Host "[*] Starting bridge server (dotnet only)..." -ForegroundColor Yellow
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "chcp 65001 | Out-Null; cd '$Root'; dotnet run --project ChromeAutomation.Bridge"
    Start-Sleep -Seconds 4
} else {
    Write-Host "[OK] Bridge already listening on port 9333" -ForegroundColor Green
}

Write-Host ""
Write-Host "Before run:" -ForegroundColor Yellow
Write-Host "  1. Keep Chrome open and logged in to CPMS"
Write-Host "  2. Refresh extension at chrome://extensions/"
Write-Host "  3. Extension popup shows connected"
Write-Host ""
Write-Host "Optional env:" -ForegroundColor Gray
Write-Host "  CPMS_UNATTENDED=1       skip Enter prompts (for Task Scheduler)"
Write-Host "  CPMS_NEW_TAB=1          force new tab instead of reusing CPMS tab"
Write-Host "  CPMS_USE_KEEP_CLICKER=1 enable UI keep-clicker (usually not needed)"
Write-Host ""

if (-not $Unattended) {
    Read-Host "Press Enter to start"
}

$keepClicker = $null
if ($env:CPMS_USE_KEEP_CLICKER -eq "1") {
    $keepScript = Join-Path $Root "click-chrome-keep.ps1"
    if (Test-Path $keepScript) {
        $keepClicker = Start-Process powershell -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $keepScript, "-DurationSec", "900"
        ) -PassThru -WindowStyle Minimized
    }
}

$logFile = Join-Path $Root "last-run.log"
try {
    dotnet run --project ChromeAutomation.CpmsExport 2>&1 | Tee-Object -FilePath $logFile -Encoding utf8
    $exitCode = $LASTEXITCODE
}
finally {
    if ($keepClicker -and -not $keepClicker.HasExited) {
        Stop-Process -Id $keepClicker.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($exitCode -ne 0) {
    Write-Host "[FAILED] Exit code: $exitCode" -ForegroundColor Red
} else {
    Write-Host "[OK] Done. Chrome was left open." -ForegroundColor Green
}

if (-not $Unattended) {
    Read-Host "Press Enter to exit"
}
exit $exitCode
