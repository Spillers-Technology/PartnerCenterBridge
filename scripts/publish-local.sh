#!/usr/bin/env bash
# Builds the Local Workbench single-file binary into artifacts/local/<rid>/ (default linux-x64).
# Needs the .NET 8 SDK and Node.js/npm; pass --skip-spa-build to embed an existing web/dist as-is.
# See scripts/publish-local.ps1 for the Windows (win-x64) build.
set -euo pipefail
rid="linux-x64"
extra=()
for arg in "$@"; do
  case "$arg" in
    --skip-spa-build) extra+=("-p:PcbBuildSpa=false") ;;
    *) rid="$arg" ;;
  esac
done
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/local/$rid"
rm -rf "$out"
dotnet publish "$root/src/PartnerCenterBridge.Api/PartnerCenterBridge.Api.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:PcbLocalWorkbench=true \
  ${extra[@]+"${extra[@]}"} -o "$out"
echo "Local Workbench: $out/PartnerCenterBridge"
