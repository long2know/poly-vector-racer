#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
bash scripts/dotnet.sh publish src/StuntRacer.Browser/StuntRacer.Browser.csproj -c Release --nologo -v minimal
bash scripts/dotnet.sh build src/StuntRacer.Host/StuntRacer.Host.csproj -c Release --nologo -v minimal
