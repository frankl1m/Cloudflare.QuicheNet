#!/bin/bash
# Builds libquiche_bindgen.so for linux-x64, linux-arm64 and linux-arm on Debian 12.
#
# Usage: scripts/debian12/build.sh [--tests] [target...]
#   target: linux-x64, linux-arm64, linux-arm (default: all three)
#
# Sources are copied to $WORK (default ~/quichenet-build) so the build doesn't
# overwrite the generated bindings in the repository, and the libraries are
# copied to $OUT/<target> (default entrega-quiche/<target>). With RUNTIMES set,
# they also go to $RUNTIMES/<target>/native, the .NET runtimes layout. The
# struct layouts that bindgen computed for each target (quiche_send_info,
# quiche_path_stats) are kept in $WORK/layout-<target>.rs.
set -euo pipefail
. "$HOME/.cargo/env"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$(cd "$SCRIPT_DIR/../.." && pwd)"
WORK="${WORK:-$HOME/quichenet-build}"
OUT="${OUT:-$SRC/entrega-quiche}"
RUNTIMES="${RUNTIMES:-}"

RUN_TESTS=0
TARGETS=()
for arg in "$@"; do
    case "$arg" in
        --tests) RUN_TESTS=1 ;;
        linux-x64|linux-arm64|linux-arm) TARGETS+=("$arg") ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done
[ ${#TARGETS[@]} -eq 0 ] && TARGETS=(linux-x64 linux-arm64 linux-arm)

triple_of() {
    case "$1" in
        linux-x64)   echo x86_64-unknown-linux-gnu ;;
        linux-arm64) echo aarch64-unknown-linux-gnu ;;
        linux-arm)   echo armv7-unknown-linux-gnueabihf ;;
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

export CC_aarch64_unknown_linux_gnu=aarch64-linux-gnu-gcc
export CXX_aarch64_unknown_linux_gnu=aarch64-linux-gnu-g++
export AR_aarch64_unknown_linux_gnu=aarch64-linux-gnu-ar
export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
export BINDGEN_EXTRA_CLANG_ARGS_aarch64_unknown_linux_gnu="--sysroot=/usr/aarch64-linux-gnu -I/usr/aarch64-linux-gnu/include"

export CC_armv7_unknown_linux_gnueabihf=arm-linux-gnueabihf-gcc
export CXX_armv7_unknown_linux_gnueabihf=arm-linux-gnueabihf-g++
export AR_armv7_unknown_linux_gnueabihf=arm-linux-gnueabihf-ar
export CARGO_TARGET_ARMV7_UNKNOWN_LINUX_GNUEABIHF_LINKER=arm-linux-gnueabihf-gcc
export BINDGEN_EXTRA_CLANG_ARGS_armv7_unknown_linux_gnueabihf="--sysroot=/usr/arm-linux-gnueabihf -I/usr/arm-linux-gnueabihf/include"

for target in "${TARGETS[@]}"; do
    triple="$(triple_of "$target")"
    echo "=== build $target ($triple)"
    # src/quiche.rs is shared by all targets: removing it makes build.rs write this target's.
    rm -f src/quiche.rs
    cargo build --release --target "$triple"
    # bindgen writes without rustfmt, so the whole file is one line: pick each check.
    grep -oE '"(Size of |Alignment of |Offset of field: )(quiche_send_info|quiche_path_stats|quiche_stats|timespec|sockaddr_storage)[^"]*"\] *\[[^]]*- *[0-9]+usize' \
        src/quiche.rs > "$WORK/layout-$target.rs"
done

echo "=== verify"
for target in "${TARGETS[@]}"; do
    lib="target/$(triple_of "$target")/release/libquiche_bindgen.so"
    echo "--- $target"
    file -b "$lib"
    echo "exports: $(nm -D --defined-only "$lib" | grep -c ' T _quiche_') with the _quiche_ prefix"
    nm -D --defined-only "$lib" \
        | grep -E ' T _quiche_(config_set_initial_rtt|conn_set_brutal_rate|conn_export_keying_material|conn_set_session)$'
    echo "max GLIBC symbol version: $(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
    echo "NEEDED: $(objdump -p "$lib" | awk '/NEEDED/ {print $2}' | tr '\n' ' ')"
    echo "layout:"
    sed -E 's/^"([^"]+)".*- *([0-9]+)usize$/  \1 = \2/' "$WORK/layout-$target.rs"
done

if ! diff -q <(tr -d '\r' < "$SRC/Cloudflare.QuicheNet/NativeMethods.g.cs") \
        "$WORK/Cloudflare.QuicheNet/NativeMethods.g.cs" > /dev/null; then
    echo "note: NativeMethods.g.cs generated on Linux differs from the one in the repository:"
    diff <(tr -d '\r' < "$SRC/Cloudflare.QuicheNet/NativeMethods.g.cs") \
        "$WORK/Cloudflare.QuicheNet/NativeMethods.g.cs" | grep '^[<>]' | head -20 || true
fi

echo "=== copy to $OUT${RUNTIMES:+ and $RUNTIMES}"
for target in "${TARGETS[@]}"; do
    lib="target/$(triple_of "$target")/release/libquiche_bindgen.so"
    mkdir -p "$OUT/$target"
    cp "$lib" "$OUT/$target/"
    sha256sum "$OUT/$target/libquiche_bindgen.so"
    if [ -n "$RUNTIMES" ]; then
        mkdir -p "$RUNTIMES/$target/native"
        cp "$lib" "$RUNTIMES/$target/native/"
    fi
done

if [ "$RUN_TESTS" = 1 ]; then
    echo "=== quiche tests (linux-x64)"
    cd "$WORK/quiche"
    cargo test -p quiche --features ffi,qlog --lib
fi

echo "=== done"
