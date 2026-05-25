# Launcher from repo root -> src\run-cpms-export.ps1
$ErrorActionPreference = "Stop"
try {
    chcp 65001 | Out-Null
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
} catch { }

& (Join-Path $PSScriptRoot "src\run-cpms-export.ps1") @args
exit $LASTEXITCODE
