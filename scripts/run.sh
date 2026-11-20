#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export STUNT_BUNDLE="$PWD/src/StuntRacer.Browser/bin/Release/net10.0/browser-wasm/AppBundle"
if [[ ! -f "$STUNT_BUNDLE/index.html" ]]; then
  bash scripts/build.sh
fi
exec bash scripts/dotnet.sh run --no-build -c Release --project src/StuntRacer.Host -- \
  --urls "http://127.0.0.1:${STUNT_PORT:-5188}"
