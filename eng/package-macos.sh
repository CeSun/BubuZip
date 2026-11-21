#!/bin/bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
eclipse_root="$project_root/external/EclipseUI"

if [[ ! -f "$eclipse_root/eng/bundle_macos.py" ]]; then
    echo "EclipseUI submodule is missing. Run: git submodule update --init --recursive" >&2
    exit 1
fi

exec python3 "$project_root/eng/package-macos.py" \
    --project "$project_root/src/BubuZip.Desktop/BubuZip.Desktop.csproj" \
    --name BubuZip \
    --executable BubuZip \
    --bundle-id io.bubuzip.app \
    --version 0.1.0 \
    --output "$project_root/out/macos/BubuZip.app" \
    "$@"
