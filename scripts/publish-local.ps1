<#
.SYNOPSIS
  Builds the Local Workbench: one self-contained PartnerCenterBridge.exe (API + embedded SPA,
  SQLite, Local accounts) in artifacts/local/<runtime>/.

.DESCRIPTION
  Needs the .NET 8 SDK and Node.js/npm (the SPA in web/ is built with npm ci + npm run build unless
  -SkipSpaBuild is given, in which case an existing web/dist is embedded as-is).
  Equivalent command:
    dotnet publish src/PartnerCenterBridge.Api -c Release -r win-x64 --self-contained `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:PcbLocalWorkbench=true `
      -o artifacts/local/win-x64

.EXAMPLE
  ./scripts/publish-local.ps1
  ./artifacts/local/win-x64/PartnerCenterBridge.exe --help
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipSpaBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/local/$Runtime"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$publishArgs = @(
    "publish", (Join-Path $root "src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj"),
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    # Symbols of every project go inside the exe, so nothing but the exe lands in the output.
    "-p:DebugType=embedded",
    "-p:PcbLocalWorkbench=true",
    "-o", $out
)
if ($SkipSpaBuild) { $publishArgs += "-p:PcbBuildSpa=false" }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Get-ChildItem $out -File | Where-Object { $_.BaseName -eq "PartnerCenterBridge" -and $_.Extension -in ".exe", "" } | Select-Object -First 1
if (-not $exe) { throw "Publish finished but PartnerCenterBridge(.exe) is missing from $out" }
Write-Host ""
Write-Host ("Local Workbench: {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
Write-Host ("Try: & '{0}' --help" -f $exe.FullName)
