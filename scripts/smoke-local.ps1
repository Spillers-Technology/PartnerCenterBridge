<#
.SYNOPSIS
  Smoke-tests a published Local Workbench exe: health check, /api/system/status profile, SPA
  fallback HTML, loopback-only binding, and a stop/restart to confirm pcb.db persists.

.DESCRIPTION
  Factored out of .github/workflows/ci.yml's local-workbench job so the release pipeline
  (.github/workflows/release.yml) can run the identical check against the signed exe before it
  goes in the release zip, instead of a second copy that could drift from CI's.

.PARAMETER ExePath
  Path to PartnerCenterBridge.exe. Defaults to the publish-local.ps1 output location.

.PARAMETER DataDir
  Working data directory for the smoke run (pcb.db, keys, ...). Created if missing. Defaults to a
  fresh folder under the temp directory.

.PARAMETER LogDir
  Where stdout/stderr logs from the launched process are written. Defaults to the temp directory.

.PARAMETER Port
  Loopback port to run on. Defaults to 5199 (matches CI's existing choice).

.EXAMPLE
  ./scripts/publish-local.ps1
  ./scripts/smoke-local.ps1
#>
param(
    [string]$ExePath = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts/local/win-x64/PartnerCenterBridge.exe"),
    [string]$DataDir = (Join-Path ([System.IO.Path]::GetTempPath()) "pcb-smoke"),
    [string]$LogDir = [System.IO.Path]::GetTempPath(),
    [int]$Port = 5199
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$exe = (Resolve-Path $ExePath -ErrorAction SilentlyContinue)
if (-not $exe) { throw "published exe not found at $ExePath" }
$exe = $exe.Path

if (Test-Path $DataDir) { Remove-Item -Recurse -Force $DataDir }
# Let the app create its data directory so it applies the private ACL used on a fresh install.
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$baseUrl = "http://localhost:$Port"

function Wait-Healthy {
    param([int]$TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing -TimeoutSec 5
            if ($resp.StatusCode -eq 200) { return $true }
        } catch {}
        Start-Sleep -Seconds 1
    }
    return $false
}

function Start-Pcb {
    Start-Process -FilePath $exe `
        -ArgumentList @("--no-browser", "--port", "$Port", "--data-dir", $DataDir) `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogDir "pcb-smoke-stdout.log") `
        -RedirectStandardError (Join-Path $LogDir "pcb-smoke-stderr.log")
}

Write-Host "==> First launch"
$proc = Start-Pcb
try {
    if (-not (Wait-Healthy)) { throw "PCB did not report healthy within 60s (first launch)" }

    $status = Invoke-RestMethod -Uri "$baseUrl/api/system/status" -UseBasicParsing
    if ($status.profile -ne "Local") {
        throw "expected /api/system/status profile 'Local', got '$($status.profile)'"
    }

    $html = Invoke-WebRequest -Uri "$baseUrl/people/abc/def" -UseBasicParsing
    if ($html.Content -notmatch '<div id="root">') {
        throw "SPA fallback for /people/abc/def did not contain <div id=`"root`">"
    }

    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop
    if (-not $listeners) { throw "no listener found on port $Port" }
    foreach ($listener in $listeners) {
        if ($listener.LocalAddress -notin @("127.0.0.1", "::1")) {
            throw "PCB is listening on a non-loopback address: $($listener.LocalAddress)"
        }
    }
    Write-Host "First launch checks passed (health, status profile, SPA fallback, loopback-only bind)."
} finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $proc.Id -Timeout 15 -ErrorAction SilentlyContinue
}

Write-Host "==> Second launch (persistence check)"
$dbPath = Join-Path $DataDir "pcb.db"
if (-not (Test-Path $dbPath)) { throw "pcb.db missing after first run: $dbPath" }

$proc2 = Start-Pcb
try {
    if (-not (Wait-Healthy)) { throw "PCB did not report healthy within 60s (second launch)" }
    if (-not (Test-Path $dbPath)) { throw "pcb.db missing after second run: $dbPath" }
    Write-Host "Second launch checks passed (pcb.db persisted, health answers)."
} finally {
    Stop-Process -Id $proc2.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $proc2.Id -Timeout 15 -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Smoke test passed: $exe"
