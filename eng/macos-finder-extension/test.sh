#!/bin/bash
set -euo pipefail
source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
staging="$(mktemp -d "${TMPDIR:-/tmp}/bubuzip-finder-tests.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
xcrun swiftc -swift-version 5 -framework AppKit -framework FinderSync \
    "$source_root/FinderRequestTransport.swift" \
    "$source_root/Tests/FinderRequestTransportTests.swift" -o "$staging/FinderRequestTransportTests"
"$staging/FinderRequestTransportTests"
