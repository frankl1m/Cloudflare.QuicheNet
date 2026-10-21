#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}"
export LIBCLANG_PATH="${LIBCLANG_PATH:-$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib}"
python3 "$root/scripts/apple/prepare-boringssl.py" tvos
cd "$root/quiche-bindgen"
patch="patch.crates-io.boring-sys.path=\"$root/quiche-bindgen/target/boring-sys-apple-tvos\""
rustup target add aarch64-apple-tvos aarch64-apple-tvos-sim
TVOS_DEPLOYMENT_TARGET=10.0 SDKROOT="$(xcrun --sdk appletvos --show-sdk-path)" python3 "$root/scripts/apple/build-target.py" aarch64-apple-tvos arm64 TVOS 10.0 cargo --config "$patch" build --release --target aarch64-apple-tvos
TVOS_DEPLOYMENT_TARGET=14.0 SDKROOT="$(xcrun --sdk appletvsimulator --show-sdk-path)" python3 "$root/scripts/apple/build-target.py" aarch64-apple-tvos-sim arm64 TVOSSIMULATOR 14.0 cargo --config "$patch" build --release --target aarch64-apple-tvos-sim
staging="$root/quiche-bindgen/target/apple"
mkdir -p "$staging/tvos-simulator"
prepare_static() {
    local target="$1"
    python3 "$root/scripts/apple/prepare-static.py" "target/$target/release/libquiche_bindgen.a" "$staging/thin/$target/libquiche_bindgen.a"
}
prepare_static aarch64-apple-tvos
prepare_static aarch64-apple-tvos-sim
if [[ "${QUICHE_TVOS_INTEL:-1}" == 1 ]]; then
    # Rust does not distribute std for the tier-3 Intel tvOS simulator target.
    # Rebuild it explicitly; never substitute an iOS simulator archive.
    if rustup run nightly rustc --version >/dev/null 2>&1; then
        rustup component add rust-src --toolchain nightly
    else
        rustup toolchain install nightly --profile minimal --component rust-src
    fi
    TVOS_DEPLOYMENT_TARGET=10.0 SDKROOT="$(xcrun --sdk appletvsimulator --show-sdk-path)" python3 "$root/scripts/apple/build-target.py" x86_64-apple-tvos x86_64 TVOSSIMULATOR 10.0 cargo +nightly --config "$patch" build -Zbuild-std=std,panic_abort --release --target x86_64-apple-tvos
    prepare_static x86_64-apple-tvos
    lipo -create "$staging/thin/x86_64-apple-tvos/libquiche_bindgen.a" "$staging/thin/aarch64-apple-tvos-sim/libquiche_bindgen.a" -output "$staging/tvos-simulator/libquiche_bindgen.a"
    lipo "$staging/tvos-simulator/libquiche_bindgen.a" -verify_arch x86_64 arm64
else
    cp "$staging/thin/aarch64-apple-tvos-sim/libquiche_bindgen.a" "$staging/tvos-simulator/"
fi
framework="$root/Cloudflare.QuicheNet.NativeAssets.iOS/Frameworks/quiche_bindgen.xcframework"
work="$(mktemp -d "$staging/tvos-xcframework.XXXXXX")"
trap 'rm -rf "$work"' EXIT
# Preserve any already-built macOS/iOS/Catalyst slices when adding tvOS.
args=()
if [[ -f "$framework/Info.plist" ]]; then
    while IFS= read -r path; do args+=(-library "$path"); done < <(python3 - "$framework" <<'PY'
import plistlib, sys
from pathlib import Path
p = Path(sys.argv[1])
for item in plistlib.loads((p / 'Info.plist').read_bytes())['AvailableLibraries']:
    if item['SupportedPlatform'] != 'tvos':
        print(p / item['LibraryIdentifier'] / item['LibraryPath'])
PY
)
fi
args+=(-library "$staging/thin/aarch64-apple-tvos/libquiche_bindgen.a" -library "$staging/tvos-simulator/libquiche_bindgen.a")
xcodebuild -create-xcframework "${args[@]}" -output "$work/quiche_bindgen.xcframework"
mkdir -p "$(dirname "$framework")"
if [[ -d "$framework" ]]; then rm -rf "$framework"; fi
mv "$work/quiche_bindgen.xcframework" "$framework"
