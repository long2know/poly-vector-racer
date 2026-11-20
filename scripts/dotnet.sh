#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
if [[ -x "$PWD/.tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$PWD/.tools/dotnet"
  export DOTNET_CLI_HOME="$PWD/.tools/cli"
  exec "$DOTNET_ROOT/dotnet" "$@"
fi
exec dotnet "$@"
