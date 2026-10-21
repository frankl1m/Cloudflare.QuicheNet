#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}"
export LIBCLANG_PATH="${LIBCLANG_PATH:-$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib}"
export SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"
python3 "$root/scripts/apple/prepare-boringssl.py"
cd "$root/quiche-bindgen"
patch="patch.crates-io.boring-sys.path=\"$root/quiche-bindgen/target/boring-sys-apple\""
for target in aarch64-apple-ios-macabi x86_64-apple-ios-macabi; do
    minimum=13.1
    [[ "$target" == aarch64-apple-ios-macabi ]] && minimum=14.0
    arch=x86_64
    [[ "$target" == aarch64* ]] && arch=arm64
    rustup target add "$target"
    IPHONEOS_DEPLOYMENT_TARGET="$minimum" python3 "$root/scripts/apple/build-target.py" "$target" "$arch" MACCATALYST "$minimum" cargo --config "$patch" build --release --target "$target"
done
