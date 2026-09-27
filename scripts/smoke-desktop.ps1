<# Smoke-test the normal GUI path without depending on a display capture. #>
param(
    [string]$ExePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/local/win-x64/PartnerCenterBridge.exe'),
    [string]$DataDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('pcb-desktop-smoke-' + [guid]::NewGuid()))
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $ExePath).Path
$versionOut = Join-Path ([System.IO.Path]::GetTempPath()) ('pcb-version-' + [guid]::NewGuid() + '.txt')
$versionErr = $versionOut + '.err'
try {
    $version = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $versionOut -RedirectStandardError $versionErr
    if ($version.ExitCode -ne 0 -or (Get-Content $versionOut -Raw) -notmatch '^\d+\.\d+\.\d+') {
        throw 'CLI version command did not return a version and exit successfully.'
    }
} finally {
    Remove-Item -LiteralPath $versionOut, $versionErr -Force -ErrorAction SilentlyContinue
}
if (Test-Path $DataDir) { throw 'Choose a fresh desktop smoke data directory.' }
$proc = Start-Process -FilePath $exe -ArgumentList @('--data-dir', $DataDir) -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    $webviewReady = $false
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Desktop process exited with code $($proc.ExitCode)" }
        if ($proc.MainWindowTitle -eq 'PartnerCenterBridge') {
            $ports = @(Get-NetTCPConnection -State Listen -OwningProcess $proc.Id -ErrorAction SilentlyContinue |
                Where-Object { $_.LocalAddress -in @('127.0.0.1', '::1') } |
                Select-Object -ExpandProperty LocalPort -Unique)
            foreach ($port in $ports) {
                try {
                    $response = Invoke-WebRequest "http://localhost:$port/health" -UseBasicParsing -TimeoutSec 2
                    if ($response.StatusCode -eq 200) { $healthy = $true; break }
                } catch { }
            }
        }
        if ($healthy -and (Test-Path (Join-Path $DataDir 'webview2'))) {
            $webviewReady = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" |
                Where-Object { $_.ParentProcessId -eq $proc.Id }).Count -gt 0
            if ($webviewReady) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw 'Desktop window did not start a healthy loopback API.' }
    if (-not $webviewReady) { throw 'WebView2 did not initialize in the desktop window.' }
    if (-not (Test-Path (Join-Path $DataDir 'pcb.db'))) { throw 'SQLite store was not created.' }
    $second = Start-Process -FilePath $exe -ArgumentList @('--data-dir', $DataDir) -PassThru -WindowStyle Hidden
    if (-not $second.WaitForExit(15000)) {
        Stop-Process -Id $second.Id -Force
        throw 'A second GUI launch did not hand off to the existing instance.'
    }
    if ($second.ExitCode -ne 0) { throw 'Second GUI launch failed.' }
    $proc.Refresh()
    if ($proc.HasExited) { throw 'Original desktop instance stopped during second-launch hand-off.' }
    if (-not $proc.CloseMainWindow() -or -not $proc.WaitForExit(15000)) {
        throw 'Closing the desktop window did not stop the application cleanly.'
    }
    if ($proc.ExitCode -ne 0) { throw "Desktop close returned exit code $($proc.ExitCode)." }
    $savedPort = [int](Get-Content (Join-Path $DataDir 'desktop-port') -Raw)
    $proc = Start-Process -FilePath $exe -ArgumentList @('--data-dir', $DataDir) -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(45)
    $restarted = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest "http://localhost:$savedPort/health" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $restarted = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $restarted) { throw 'Desktop restart did not reuse its available loopback origin.' }
    $proc.Refresh()
    if (-not $proc.CloseMainWindow() -or -not $proc.WaitForExit(15000)) { throw 'Desktop restart did not close cleanly.' }
    Write-Host "Desktop smoke passed: version output, window, loopback API, SQLite, WebView2, single instance, clean shutdown and origin reuse."
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
