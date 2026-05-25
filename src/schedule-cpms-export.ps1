# Unattended CPMS export for Windows Task Scheduler
# Chrome must already be running with extension connected.

$env:CPMS_UNATTENDED = "1"
& (Join-Path $PSScriptRoot "run-cpms-export.ps1")
exit $LASTEXITCODE
