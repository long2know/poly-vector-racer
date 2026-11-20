#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .tools/dotnet .tools/cli .tools/tmp
curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh \
  --output .tools/dotnet-install.sh
bash .tools/dotnet-install.sh --version 10.0.100 --install-dir "$PWD/.tools/dotnet" --no-path
bash scripts/dotnet.sh workload install wasm-tools --skip-manifest-update \
  --temp-dir "$PWD/.tools/tmp"
