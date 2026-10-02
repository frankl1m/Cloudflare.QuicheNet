# Android build

Builds `libquiche_bindgen.so` for Android: `arm64-v8a`, `armeabi-v7a`, `x86` and `x86_64`, for API level 21 and later (the minimum of .NET for Android). It runs on Debian 12, the same WSL distribution as the Linux build.

From Windows, through WSL, after `scripts/debian12/setup.sh`:

```powershell
wsl -d Debian12 --user root bash scripts/android/setup.sh          # once: NDK r30, the Rust targets and cargo-ndk
wsl -d Debian12 --user root bash scripts/android/build.sh          # the four ABIs
wsl -d Debian12 --user root bash scripts/android/build.sh x86_64   # only some ABIs
```

- **`setup.sh`** downloads the Android NDK r30 for Linux from `dl.google.com`, checks its SHA-1 and unpacks it in `/opt/android-ndk-r30`, linked as `/opt/android-ndk`. It also adds the four Rust targets and installs `cargo-ndk`. NDK r28 and later align 64-bit libraries to 16 KB, which Google Play requires for Android 15 and later.
- **`build.sh`** copies the sources to `~/quichenet-build-android`, so the bindings generated in the repository aren't overwritten, and builds each ABI with `cargo ndk -t <abi> -P 21`. Each stripped library goes to `$OUT/<rid>/native/libquiche_bindgen.so`, with the .NET runtime identifier: `android-arm64`, `android-arm`, `android-x86` and `android-x64`. `OUT` defaults to `~/quichenet-build-android/runtimes`; set `API` for another minimum API level.

For each ABI, `build.sh` checks and prints:

- the exports: 173 `_quiche_` functions;
- NEEDED: only system libraries, never `libc++_shared.so`, and no unresolved libc++ symbols;
- the LOAD alignment: `0x4000` on the 64-bit ABIs;
- that it loads on the oldest API it targets (21, Android 5.0, the lowest that .NET for Android, NDK r26+ and Rust support): `DT_HASH` besides `DT_GNU_HASH` (alone, API 23+), no packed relocations (RELR is API 28+, Android's APS2 API 23+), no ELF TLS (API 29+), and every strong symbol it imports present in the API 21 libraries. The weak ones, like the `getrandom` (API 28) that BoringSSL uses, resolve to null on older devices and BoringSSL falls back to the system call;
- the size and offsets that bindgen computed with the NDK headers for `quiche_send_info`, `quiche_path_stats` and `quiche_stats`, kept in `~/quichenet-build-android/layout-<abi>.rs`.

On the 32-bit ABIs, `timespec` is `{i32, i32}` and `sockaddr_storage` is 4-aligned, and `u64` is 4-aligned on `x86`, so those structs differ from the 64-bit ones: a C# wrapper has to read them with these offsets.

The NDK r30 headers reject a clang target without the API level. cargo-ndk sets `BINDGEN_EXTRA_CLANG_ARGS_<triple>` without one, so `build.sh` passes the same variable with dashes, which bindgen reads first, with `--target=<triple><API>`.

The header copy, `quiche-bindgen/include/quiche.h`, has to declare `quiche_config_set_initial_rtt`; `build.sh` stops if it doesn't.

Run the `wsl` commands from the repository root. From Git Bash, set `MSYS_NO_PATHCONV=1` so a path passed in `OUT` reaches WSL unchanged.
