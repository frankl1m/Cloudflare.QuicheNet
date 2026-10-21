#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
target="$1" arch="$2" platform="$3" minimum="$4"
archive="$root/quiche-bindgen/target/$target/release/libquiche_bindgen.a"
if [[ -d "$root/quiche-bindgen/target/$target" ]]; then
    if [[ -f "$archive" ]] && python3 "$root/scripts/apple/deployment.py" "$archive" "$arch" "$platform" "$minimum"; then
        exit 0
    fi
    # Cargo does not reliably fingerprint deployment environment variables.
    # Rebuild incompatible objects instead of changing their load commands.
    cargo clean --manifest-path "$root/quiche-bindgen/Cargo.toml" --release --target "$target"
fi

mkdir -p "$root/quiche-bindgen/target/apple/deployment-flags"
touch "$root/quiche-bindgen/target/apple/deployment-flags/$target"
