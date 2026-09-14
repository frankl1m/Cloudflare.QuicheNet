#!/bin/bash
# Installs the toolchain to build quiche-bindgen for linux-x64 and linux-arm64
# on Debian 12. Run as root.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y --no-install-recommends \
    ca-certificates curl git rsync file binutils \
    build-essential cmake ninja-build perl golang pkg-config \
    clang libclang-dev \
    gcc-aarch64-linux-gnu g++-aarch64-linux-gnu libc6-dev-arm64-cross

if [ ! -x "$HOME/.cargo/bin/rustup" ]; then
    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable
fi
. "$HOME/.cargo/env"
rustup target add x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu

echo "--- versions"
cat /etc/debian_version
ldd --version | head -1
rustc --version
cmake --version | head -1
clang --version | head -1
aarch64-linux-gnu-gcc --version | head -1
