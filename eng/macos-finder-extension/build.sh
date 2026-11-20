#!/bin/bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
rid="osx-arm64"
output=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid|--output)
            if [[ $# -lt 2 ]]; then echo "Missing value for $1" >&2; exit 2; fi
            if [[ "$1" == "--rid" ]]; then rid="$2"; else output="$2"; fi
            shift 2 ;;
        *) echo "Usage: $0 --rid osx-arm64|osx-x64 --output /path/EclipseArchiveFinder.appex" >&2; exit 2 ;;
    esac
done
case "$rid" in
    osx-arm64) arch="arm64" ;;
    osx-x64) arch="x86_64" ;;
    *) echo "Unsupported macOS runtime: $rid" >&2; exit 2 ;;
esac
if [[ -z "$output" || "$output" != *.appex ]]; then echo "--output must end in .appex" >&2; exit 2; fi
if [[ -e "$output" ]]; then echo "Output already exists: $output" >&2; exit 2; fi
staging="$(mktemp -d "${TMPDIR:-/tmp}/eclipsearchive-finder.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
xcodebuild -project "$source_root/FinderExtension.xcodeproj" \
    -target EclipseArchiveFinder -configuration Release -quiet \
    SDKROOT=macosx ARCHS="$arch" ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO \
    SYMROOT="$staging/products" OBJROOT="$staging/objects" build
mkdir -p "$(dirname "$output")"
ditto "$staging/products/Release/EclipseArchiveFinder.appex" "$output"
echo "Built unsigned Finder extension: $output"
