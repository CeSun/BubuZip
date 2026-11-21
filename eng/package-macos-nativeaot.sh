#!/bin/bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="osx-arm64"
if [[ "$(uname -m)" == "x86_64" ]]; then rid="osx-x64"; fi
configuration="Release"
bundle_args=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid|--configuration)
            if [[ $# -lt 2 ]]; then echo "Missing value for $1" >&2; exit 2; fi
            if [[ "$1" == "--rid" ]]; then rid="$2"; else configuration="$2"; fi
            shift 2 ;;
        --rid=*) rid="${1#*=}"; shift ;;
        --configuration=*) configuration="${1#*=}"; shift ;;
        --publish-dir|--publish-dir=*)
            echo "This script creates its own NativeAOT publish directory." >&2; exit 2 ;;
        --help|-h)
            echo "Usage: $0 [--rid osx-arm64|osx-x64] [--configuration Release] [macOS packager options]"
            exit 0 ;;
        *) bundle_args+=("$1"); shift ;;
    esac
done

case "$rid" in osx-arm64|osx-x64) ;; *) echo "Unsupported macOS RID: $rid" >&2; exit 2 ;; esac
publish_dir="$project_root/out/nativeaot/$rid/BubuZip"
output="$project_root/out/macos-nativeaot/$rid/BubuZip.app"

dotnet publish "$project_root/src/BubuZip.Desktop/BubuZip.Desktop.csproj" \
    -c "$configuration" -r "$rid" --self-contained true \
    -p:BubuZipNativeAot=true -p:TrimmerSingleWarn=false -o "$publish_dir"

# Debug symbols stay in the publish directory, outside the signed application.
staging="$(mktemp -d "$project_root/out/nativeaot/.bundle-$rid.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
python3 - "$publish_dir" "$staging" <<'PY'
from pathlib import Path
import shutil
import sys

source, destination = map(Path, sys.argv[1:])
if any(source.rglob("*.dll")) or any(source.glob("*.runtimeconfig.json")):
    raise SystemExit("NativeAOT output unexpectedly contains managed runtime files.")
for item in source.iterdir():
    if item.suffix in {".dSYM", ".pdb", ".dbg", ".dwarf"}:
        continue
    target = destination / item.name
    if item.is_dir():
        shutil.copytree(item, target, symlinks=True)
    else:
        shutil.copy2(item, target)
PY

"$project_root/eng/package-macos.sh" --publish-dir "$staging" \
    --rid "$rid" --configuration "$configuration" --output "$output" ${bundle_args[@]+"${bundle_args[@]}"}
