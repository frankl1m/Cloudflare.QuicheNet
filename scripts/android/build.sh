#!/bin/bash
# Builds libquiche_bindgen.so for Android with the NDK installed by setup.sh.
#
# Usage: scripts/android/build.sh [abi...]
#   abi: arm64-v8a, armeabi-v7a, x86, x86_64 (default: all four)
#
# Sources are copied to $WORK (default ~/quichenet-build-android) so the build
# doesn't overwrite the generated bindings in the repository. Each library goes
# to $OUT/<rid>/native/libquiche_bindgen.so, with the .NET runtime identifier
# (android-arm64, android-arm, android-x86, android-x64); OUT defaults to
# $WORK/runtimes. The struct layouts that bindgen computed for each target
# (quiche_send_info, quiche_path_stats) are kept in $WORK/layout-<abi>.rs.
set -euo pipefail
. "$HOME/.cargo/env"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$(cd "$SCRIPT_DIR/../.." && pwd)"
WORK="${WORK:-$HOME/quichenet-build-android}"
OUT="${OUT:-$WORK/runtimes}"
API="${API:-21}"   # the minimum API level of .NET for Android
export ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-/opt/android-ndk}"
LLVM="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64"
SYSROOT="$LLVM/sysroot"

ABIS=("$@")
[ ${#ABIS[@]} -eq 0 ] && ABIS=(arm64-v8a armeabi-v7a x86 x86_64)

triple_of() {
    case "$1" in
        arm64-v8a)   echo aarch64-linux-android ;;
        armeabi-v7a) echo armv7-linux-androideabi ;;
        x86)         echo i686-linux-android ;;
        x86_64)      echo x86_64-linux-android ;;
        *) echo "Unknown ABI: $1" >&2; exit 2 ;;
    esac
}
# The clang triple and the sysroot include directory, where they differ from Rust's.
clang_triple_of() {
    case "$1" in
        armv7-linux-androideabi) echo armv7a-linux-androideabi ;;
        *) echo "$1" ;;
    esac
}
include_of() {
    case "$1" in
        armv7-linux-androideabi) echo arm-linux-androideabi ;;
        *) echo "$1" ;;
    esac
}
rid_of() {
    case "$1" in
        arm64-v8a)   echo android-arm64 ;;
        armeabi-v7a) echo android-arm ;;
        x86)         echo android-x86 ;;
        x86_64)      echo android-x64 ;;
    esac
}

if ! grep -q 'quiche_config_set_initial_rtt' "$SRC/quiche-bindgen/include/quiche.h"; then
    echo "quiche-bindgen/include/quiche.h does not declare quiche_config_set_initial_rtt" >&2
    exit 1
fi

echo "=== sync sources to $WORK"
mkdir -p "$WORK/Cloudflare.QuicheNet"
rsync -a --delete --exclude /target --exclude .git "$SRC/quiche/" "$WORK/quiche/"
rsync -a --delete --exclude /target --exclude /src/quiche.rs --exclude /src/quiche_ffi.rs \
    "$SRC/quiche-bindgen/" "$WORK/quiche-bindgen/"

cd "$WORK/quiche-bindgen"

for abi in "${ABIS[@]}"; do
    triple="$(triple_of "$abi")"
    rid="$(rid_of "$abi")"
    lib="target/$triple/release/libquiche_bindgen.so"

    echo "=== build $abi ($triple, API $API)"
    # bindgen (ours and boring-sys's) parses the NDK headers, so the layout
    # checks it emits are the target's, not the host's. cargo-ndk sets
    # BINDGEN_EXTRA_CLANG_ARGS_<triple with underscores> without a --target, and
    # the headers of r30 reject a triple without the API level; bindgen looks up
    # the variable with dashes first, so this one wins. src/quiche.rs is shared
    # by all targets: removing it makes build.rs write this target's.
    rm -f src/quiche.rs
    env "BINDGEN_EXTRA_CLANG_ARGS_$triple=--target=$(clang_triple_of "$triple")$API --sysroot=$SYSROOT -I$SYSROOT/usr/include/$(include_of "$triple")" \
        cargo ndk -t "$abi" -P "$API" build --release
    # bindgen writes without rustfmt, so the whole file is one line: pick each check.
    grep -oE '"(Size of |Alignment of |Offset of field: )(quiche_send_info|quiche_path_stats|quiche_stats|timespec|sockaddr_storage)[^"]*"\] *\[[^]]*- *[0-9]+usize' \
        src/quiche.rs > "$WORK/layout-$abi.rs"

    "$LLVM/bin/llvm-strip" --strip-unneeded "$lib"

    echo "--- verify $abi"
    file -b "$lib"
    exports="$("$LLVM/bin/llvm-nm" -D --defined-only "$lib" | grep -c ' T _quiche_')"
    echo "exports: $exports with the _quiche_ prefix"
    "$LLVM/bin/llvm-nm" -D --defined-only "$lib" \
        | grep -E ' T _quiche_(config_set_initial_rtt|conn_set_brutal_rate|conn_export_keying_material)$'
    needed="$("$LLVM/bin/llvm-readelf" -d "$lib" | awk '/NEEDED/ {gsub(/[\[\]]/, "", $NF); print $NF}' | tr '\n' ' ')"
    echo "NEEDED: $needed"
    if echo "$needed" | grep -q 'c++_shared'; then
        echo "error: $abi needs libc++_shared.so, which apps don't ship by default" >&2
        exit 1
    fi
    if "$LLVM/bin/llvm-nm" -D --undefined-only "$lib" | grep -q '__ndk1'; then
        echo "error: $abi has unresolved libc++ symbols:" >&2
        "$LLVM/bin/llvm-nm" -D --undefined-only "$lib" | grep '__ndk1' | head >&2
        exit 1
    fi
    echo "LOAD alignment: $("$LLVM/bin/llvm-readelf" -lW "$lib" | awk '$1 == "LOAD" {print $NF}' | sort -u | tr '\n' ' ')"
    echo "layout:"
    sed -E 's/^"([^"]+)".*- *([0-9]+)usize$/  \1 = \2/' "$WORK/layout-$abi.rs"

    mkdir -p "$OUT/$rid/native"
    cp "$lib" "$OUT/$rid/native/libquiche_bindgen.so"
done

echo "=== copied to $OUT"
for abi in "${ABIS[@]}"; do
    sha256sum "$OUT/$(rid_of "$abi")/native/libquiche_bindgen.so"
done
echo "=== done"
