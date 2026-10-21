#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
mode="${1:-all}"
case "$mode" in all|ios|maccatalyst|macos|tvos) ;; *) echo 'Usage: build.sh [all|ios|maccatalyst|macos|tvos]' >&2; exit 2;; esac
for tool in cargo rustup cmake xcrun lipo xcodebuild; do
    command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-2}"
export LIBCLANG_PATH="${LIBCLANG_PATH:-$(xcode-select -p)/Toolchains/XcodeDefault.xctoolchain/usr/lib}"
staging="$root/quiche-bindgen/target/apple"
mkdir -p "$staging"
cd "$root/quiche-bindgen"
build_target() {
    local target="$1" sdk="$2" minimum=10.0
    [[ "$target" == aarch64-apple-ios-sim ]] && minimum=14.0
    local arch=arm64 platform=IOS
    [[ "$target" == x86_64* ]] && arch=x86_64
    [[ "$sdk" == iphonesimulator ]] && platform=IOSSIMULATOR
    rustup target add "$target"
    IPHONEOS_DEPLOYMENT_TARGET="$minimum" SDKROOT="$(xcrun --sdk "$sdk" --show-sdk-path)" python3 "$root/scripts/apple/build-target.py" "$target" "$arch" "$platform" "$minimum" cargo build --release --target "$target"
}
prepare_static() {
    local target="$1"
    python3 "$root/scripts/apple/prepare-static.py" "target/$target/release/libquiche_bindgen.a" "$staging/thin/$target/libquiche_bindgen.a"
}
fat_static() {
    local name="$1" first="$2" second="$3"
    mkdir -p "$staging/$name"
    prepare_static "$first"
    prepare_static "$second"
    lipo -create "$staging/thin/$first/libquiche_bindgen.a" "$staging/thin/$second/libquiche_bindgen.a" -output "$staging/$name/libquiche_bindgen.a"
    lipo "$staging/$name/libquiche_bindgen.a" -verify_arch x86_64 arm64
}
if [[ "$mode" == tvos ]]; then
    exec "$root/scripts/apple/build-tvos.sh"
fi
if [[ "$mode" == all || "$mode" == macos ]]; then
    "$root/scripts/macos/build.sh" universal
    fat_static macos x86_64-apple-darwin aarch64-apple-darwin
fi
if [[ "$mode" == all || "$mode" == ios ]]; then
    build_target aarch64-apple-ios iphoneos
    build_target aarch64-apple-ios-sim iphonesimulator
    build_target x86_64-apple-ios iphonesimulator
    prepare_static aarch64-apple-ios
    fat_static simulator x86_64-apple-ios aarch64-apple-ios-sim
fi
if [[ "$mode" == all || "$mode" == maccatalyst ]]; then
    "$root/scripts/apple/build-catalyst.sh"
    fat_static maccatalyst x86_64-apple-ios-macabi aarch64-apple-ios-macabi
fi
# Never combine different Apple platforms with lipo. XCFramework keeps each
# platform separate while each simulator/Mac/Catalyst slice is universal.
args=()
[[ "$mode" == all || "$mode" == macos ]] && args+=(-library "$staging/macos/libquiche_bindgen.a")
if [[ "$mode" == all || "$mode" == ios ]]; then
    args+=(-library "$staging/thin/aarch64-apple-ios/libquiche_bindgen.a" -library "$staging/simulator/libquiche_bindgen.a")
fi
[[ "$mode" == all || "$mode" == maccatalyst ]] && args+=(-library "$staging/maccatalyst/libquiche_bindgen.a")
# Use a fresh output directory; replace the previous generated bundle only
# after xcodebuild has successfully created this one.
work="$(mktemp -d "$staging/xcframework.XXXXXX")"
trap 'rm -rf "$work"' EXIT
xcodebuild -create-xcframework "${args[@]}" -output "$work/quiche_bindgen.xcframework"
destination="$root/Cloudflare.QuicheNet.NativeAssets.iOS/Frameworks"
mkdir -p "$destination"
if [[ -d "$destination/quiche_bindgen.xcframework" ]]; then rm -rf "$destination/quiche_bindgen.xcframework"; fi
mv "$work/quiche_bindgen.xcframework" "$destination/"

if [[ "$mode" == all ]]; then "$root/scripts/apple/build-tvos.sh"; fi
