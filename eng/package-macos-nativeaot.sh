#!/bin/bash
set -euo pipefail

# Compatibility entry point: the standard macOS packager now always uses NativeAOT.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$project_root/eng/package-macos.sh" "$@"
