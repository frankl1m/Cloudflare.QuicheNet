#!/bin/bash
# Installs the toolchain to build quiche-bindgen for Android (arm64-v8a,
# armeabi-v7a, x86 and x86_64) on Debian 12. Run as root, after
# scripts/debian12/setup.sh (Rust, cmake, clang, Go and perl come from there).
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

# NDK r30. r28 and later align 64-bit libraries to 16 KB by default, which
# Google Play requires for Android 15 and later.
NDK_VERSION=r30
NDK_ZIP=android-ndk-$NDK_VERSION-linux.zip
NDK_SHA1=5107f898313790e449e87eee2183d9a20602dee9
NDK_DIR=/opt/android-ndk-$NDK_VERSION

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl unzip

if [ ! -d "$NDK_DIR" ]; then
    tmp="$(mktemp -d)"
    curl -fL --proto '=https' --tlsv1.2 -o "$tmp/$NDK_ZIP" "https://dl.google.com/android/repository/$NDK_ZIP"
    echo "$NDK_SHA1  $tmp/$NDK_ZIP" | sha1sum -c -
    unzip -q "$tmp/$NDK_ZIP" -d /opt
    mv "/opt/android-ndk-$NDK_VERSION" "$NDK_DIR" 2>/dev/null || true
    rm -rf "$tmp"
fi
ln -sfn "$NDK_DIR" /opt/android-ndk

. "$HOME/.cargo/env"
rustup target add aarch64-linux-android armv7-linux-androideabi i686-linux-android x86_64-linux-android
cargo install cargo-ndk --locked

echo "--- versions"
grep Pkg.Revision /opt/android-ndk/source.properties
cargo ndk --version
rustc --version
