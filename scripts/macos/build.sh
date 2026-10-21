#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
mode="${1:-universal}"
case "$mode" in x64|arm64|universal) ;; *) echo 'Usage: build.sh [x64|arm64|universal]' >&2; exit 2;; esac
for tool in cargo rustup cmake xcrun lipo codesign; do
    command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
export SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}"
export LIBCLANG_PATH="${LIBCLANG_PATH:-$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib}"
cd "$root/quiche-bindgen"
build_arch() {
    local arch="$1" target="$2" rid="$3"
    local minimum=11.0
    [[ "$arch" == x86_64 ]] && minimum=10.12
    export MACOSX_DEPLOYMENT_TARGET="$minimum"
    rustup target add "$target"
    python3 "$root/scripts/apple/build-target.py" "$target" "$arch" MACOS "$minimum" cargo build --release --target "$target"
    local output="$root/Cloudflare.QuicheNet.NativeAssets.macOS/runtimes/osx-$rid/native"
    mkdir -p "$output"
    cp "target/$target/release/libquiche_bindgen.dylib" "$output/"
    install_name_tool -id '@rpath/libquiche_bindgen.dylib' "$output/libquiche_bindgen.dylib"
    codesign --force --sign - "$output/libquiche_bindgen.dylib"
    lipo "$output/libquiche_bindgen.dylib" -verify_arch "$arch"
}
if [[ "$mode" == x64 || "$mode" == universal ]]; then build_arch x86_64 x86_64-apple-darwin x64; fi
if [[ "$mode" == arm64 || "$mode" == universal ]]; then build_arch arm64 aarch64-apple-darwin arm64; fi
if [[ "$mode" == universal ]]; then
    assets="$root/Cloudflare.QuicheNet.NativeAssets.macOS/runtimes"
    mkdir -p "$assets/osx-universal/native"
    lipo -create "$assets/osx-x64/native/libquiche_bindgen.dylib" "$assets/osx-arm64/native/libquiche_bindgen.dylib" -output "$assets/osx-universal/native/libquiche_bindgen.dylib"
    lipo "$assets/osx-universal/native/libquiche_bindgen.dylib" -verify_arch x86_64 arm64
    codesign --force --sign - "$assets/osx-universal/native/libquiche_bindgen.dylib"
    # Each standard RID also receives the universal binary, so normal NuGet RID
    # selection works without inventing a runtime identifier for universal apps.
    cp "$assets/osx-universal/native/libquiche_bindgen.dylib" "$assets/osx-x64/native/"
    cp "$assets/osx-universal/native/libquiche_bindgen.dylib" "$assets/osx-arm64/native/"
    lipo -info "$assets/osx-universal/native/libquiche_bindgen.dylib"
    python3 "$root/scripts/macos/verify.py" "$assets/osx-universal/native/libquiche_bindgen.dylib"
fi
