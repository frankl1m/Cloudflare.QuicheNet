#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
library="$root/Cloudflare.QuicheNet.NativeAssets.macOS/runtimes/osx-universal/native"
output="$root/quiche-bindgen/target/apple/smoke"
python3 "$root/scripts/macos/verify.py" "$library/libquiche_bindgen.dylib"
mkdir -p "$output"
cp "$library/libquiche_bindgen.dylib" "$output/"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
for arch in x86_64 arm64; do
    minimum=11.0
    [[ "$arch" == x86_64 ]] && minimum=10.12
    target="$arch-apple-macos$minimum"
    xcrun --sdk macosx clang -target "$target" -isysroot "$sdk" \
        "$root/scripts/apple/native-smoke.c" -L "$library" -lquiche_bindgen \
        -Wl,-rpath,@executable_path -o "$output/$target-dynamic"
done
