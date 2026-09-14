#!/bin/bash
# Builds libquiche_bindgen.so for linux-x64 and linux-arm64 on Debian 12.
#
# Usage: scripts/debian12/build.sh [--tests]
#
# Sources are copied to $WORK (default ~/quichenet-build) so the build doesn't
# overwrite the generated bindings in the repository, and the libraries are
# copied to $OUT (default entrega-quiche/linux-x64 and linux-arm64).
set -euo pipefail
. "$HOME/.cargo/env"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$(cd "$SCRIPT_DIR/../.." && pwd)"
WORK="${WORK:-$HOME/quichenet-build}"
OUT="${OUT:-$SRC/entrega-quiche}"

RUN_TESTS=0
for arg in "$@"; do
    case "$arg" in
        --tests) RUN_TESTS=1 ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done

echo "=== sync sources to $WORK"
mkdir -p "$WORK/Cloudflare.QuicheNet"
rsync -a --delete --exclude /target --exclude .git "$SRC/quiche/" "$WORK/quiche/"
rsync -a --delete --exclude /target --exclude /src/quiche.rs --exclude /src/quiche_ffi.rs \
    "$SRC/quiche-bindgen/" "$WORK/quiche-bindgen/"

cd "$WORK/quiche-bindgen"

echo "=== build linux-x64"
cargo build --release --target x86_64-unknown-linux-gnu

echo "=== build linux-arm64"
export CC_aarch64_unknown_linux_gnu=aarch64-linux-gnu-gcc
export CXX_aarch64_unknown_linux_gnu=aarch64-linux-gnu-g++
export AR_aarch64_unknown_linux_gnu=aarch64-linux-gnu-ar
export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
export BINDGEN_EXTRA_CLANG_ARGS_aarch64_unknown_linux_gnu="--sysroot=/usr/aarch64-linux-gnu -I/usr/aarch64-linux-gnu/include"
cargo build --release --target aarch64-unknown-linux-gnu

echo "=== verify"
for triple in x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu; do
    lib="target/$triple/release/libquiche_bindgen.so"
    echo "--- $triple"
    file -b "$lib"
    echo "exports: $(nm -D --defined-only "$lib" | grep -c ' T _quiche_') with the _quiche_ prefix"
    nm -D --defined-only "$lib" | grep -E ' T _quiche_conn_(set_brutal_rate|export_keying_material|set_session)$'
    echo "max GLIBC symbol version: $(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
    echo "NEEDED: $(objdump -p "$lib" | awk '/NEEDED/ {print $2}' | tr '\n' ' ')"
done

if ! diff -q <(tr -d '\r' < "$SRC/Cloudflare.QuicheNet/NativeMethods.g.cs") \
        "$WORK/Cloudflare.QuicheNet/NativeMethods.g.cs" > /dev/null; then
    echo "note: NativeMethods.g.cs generated on Linux differs from the one in the repository:"
    diff <(tr -d '\r' < "$SRC/Cloudflare.QuicheNet/NativeMethods.g.cs") \
        "$WORK/Cloudflare.QuicheNet/NativeMethods.g.cs" | grep '^[<>]' | head -20 || true
fi

echo "=== copy to $OUT"
mkdir -p "$OUT/linux-x64" "$OUT/linux-arm64"
cp target/x86_64-unknown-linux-gnu/release/libquiche_bindgen.so "$OUT/linux-x64/"
cp target/aarch64-unknown-linux-gnu/release/libquiche_bindgen.so "$OUT/linux-arm64/"
sha256sum "$OUT/linux-x64/libquiche_bindgen.so" "$OUT/linux-arm64/libquiche_bindgen.so"

if [ "$RUN_TESTS" = 1 ]; then
    echo "=== quiche tests (linux-x64)"
    cd "$WORK/quiche"
    cargo test -p quiche --features ffi,qlog --lib
fi

echo "=== done"
