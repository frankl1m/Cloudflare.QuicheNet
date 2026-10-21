#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
directory="${1:-$root/quiche-bindgen/target/apple/smoke}"
expected="${2:-}"
os="$(sw_vers -productVersion)"
arch="$(uname -m)"
printf 'Actual runtime: macOS %s; architecture %s\n' "$os" "$arch"
if [[ -n "$expected" && "$os" != "$expected" && "$os" != "$expected".* ]]; then
    printf 'FAIL: requested macOS %s, running %s\n' "$expected" "$os" >&2
    exit 1
fi
case "$arch" in
    x86_64) target="x86_64-apple-macos10.12" ;;
    arm64) target="arm64-apple-macos11.0" ;;
    *) printf 'Unsupported runtime architecture: %s\n' "$arch" >&2; exit 1 ;;
esac
found=0
for suffix in "" -dynamic; do
    executable="$directory/$target$suffix"
    if [[ -x "$executable" ]]; then
        found=1
        printf 'Running %s\n' "$(basename "$executable")"
        "$executable"
    fi
done
if [[ "$found" == 0 ]]; then
    printf 'Missing smoke executable in %s\nBuild with scripts/apple/verify.py --link or scripts/macos/build-runtime-smoke.sh\n' "$directory" >&2
    exit 1
fi
if [[ -n "$expected" ]]; then
    printf 'PASS requested OS runtime: %s\n' "$expected"
fi
