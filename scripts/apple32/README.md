# Isolated Apple32 experiment

These helpers attempt iOS 10 ARMv7s device and i386 simulator builds only when an
extracted older Apple SDK is available. They do not alter `xcode-select`, the
64-bit Cargo target, or the verified six-platform XCFramework. iOS 11 and later
run 64-bit apps; iOS 12+ support uses the completed 64-bit assets.

First verify that the older SDK can actually link both architectures:

```sh
python3 scripts/apple32/build.py /path/to/Xcode.app --probe-only
```

Then build with the installed nightly toolchain and `rust-src`:

```sh
CARGO_BUILD_JOBS=4 python3 scripts/apple32/build.py /path/to/Xcode.app
```

The helper copies the small wrapper workspace, uses a separate patched
`boring-sys 4.22.0` source and rebuilds Rust std with `-Zbuild-std`. All state and
outputs stay under `quiche-bindgen/target/apple32/`; final thin archives and C
probes are under `artifacts/armv7s/` and `artifacts/i386/`. The native source of
quiche is shared read-only. An OS file lock prevents overlapping Apple32 runs.
Current clang is selected explicitly. Target SDK/header lookup uses absolute
paths from the extracted Xcode through `SDKROOT` and the isolated BoringSSL patch.
Host build scripts retain the current developer tools and macOS SDK.

A successful result must pass SDK linking, per-object CPU/platform/deployment
checks, byte-identical duplicate removal without public symbol loss, all managed
entry-point exports, and a full force-load link against the older SDK. Passing
these checks does not claim execution on an iOS 10 device/simulator, a working
32-bit .NET app, or a new XCFramework slice. Those require separate validation.
Keep failed attempts isolated and record the actual compiler/linker error.

After both archives pass, audit the real private Rust FFI structures and create a
candidate bundle while preserving the complete Apple64 backup:

```sh
python3 scripts/apple32/audit-layout.py
python3 scripts/apple32/package.py
python3 scripts/apple/verify.py quiche-bindgen/target/apple32/quiche_bindgen.xcframework \
  --include-ios32 --legacy-xcode /path/to/Xcode.app --link
```

The package helper combines existing 64-bit objects with validated 32-bit archives;
it never recompiles or replaces the installed bundle. Install the candidate only
after verification. `target/apple32/backup/quiche_bindgen.xcframework` retains the
original 64-bit distribution. The general verifier autodetects either complete
matrix; `--include-ios32` explicitly requires both legacy architectures.

`target/apple32/build-info.json` records each architecture separately, including
its exact Cargo lock, generated binding/input hashes, patched build script,
Rust/std/clang and SDK 12.4 metadata, native artifact hashes and real Rust FFI
layout audit. The Apple64 snapshot remains separate. Current results confirm
173 exports and native force-load linking for ARMv7s and i386 at iOS 10, plus
Stats 172/PathStats 396/SendInfo 272 with alignment 4 and C-compatible offsets.
These results do not establish runtime execution on iOS 10.

A reproducible managed link scaffold and toolchain flags are documented in
[`managed-smoke/README.md`](managed-smoke/README.md). Its build is separate from
the native verification above and does not claim 32-bit runtime execution.
