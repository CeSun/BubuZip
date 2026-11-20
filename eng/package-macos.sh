#!/bin/bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
eclipse_root="$project_root/external/EclipseUI"

if [[ ! -f "$eclipse_root/eng/bundle_macos.py" ]]; then
    echo "EclipseUI submodule is missing. Run: git submodule update --init --recursive" >&2
    exit 1
fi

exec python3 "$project_root/eng/package-macos.py" \
    --project "$project_root/src/EclipseArchive.Desktop/EclipseArchive.Desktop.csproj" \
    --name EclipseArchive \
    --executable EclipseArchive.Desktop \
    --bundle-id io.eclipsearchive.app \
    --version 0.1.0 \
    --output "$project_root/out/macos/EclipseArchive.app" \
    "$@"
