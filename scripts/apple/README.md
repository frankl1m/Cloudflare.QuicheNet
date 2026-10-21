# Apple builds

The fork includes the QUIC wrapper improvements from Socks5Lib commit `0784898d`:
event-driven stream/datagram pumps, pooled receive buffers, bounded queues,
backpressure, FIN/reset and cancellation handling, orderly native disposal,
HTTP/3 bindings, TLS session resumption/exporters, anti-replay checks for 0-RTT,
path MTU discovery, and the Android/Unix 32-bit native layout fixes. The wrapper
has no dependency on Socks5Lib. Its original public session, exporter, congestion
control and flow-control APIs remain available.

## Native platforms

| Platform | Architectures | Native format |
| --- | --- | --- |
| macOS | Intel x86_64 10.12+ / Apple Silicon arm64 11+ | Universal dylib and static XCFramework slice |
| iOS device | arm64 10+ | Static XCFramework slice |
| iOS simulator | x86_64 10+ / arm64 14+ | Universal static XCFramework slice |
| Mac Catalyst | x86_64 13.1+ / arm64 14+ | Universal static XCFramework slice |
| tvOS device | arm64 10+ | Static XCFramework slice |
| tvOS simulator | x86_64 10+ / arm64 14+ | Universal static XCFramework slice |

These are native library deployment targets. A consuming .NET SDK/workload can
require a newer app minimum (for example, .NET 10 Catalyst requires 15.0 in the
installed workload); set the application's `SupportedOSPlatformVersion` accordingly.

The managed .NET 10 library targets iOS/tvOS 12.2+ and Catalyst 13.1+;
the installed Catalyst app workload requires 15+. On iOS/tvOS 12.2,
.NET's `AesGcm` is unavailable because CryptoKit requires 13+. Server anti-replay
therefore rejects 0-RTT while keeping Initial, Handshake and 1-RTT available;
server 0-RTT acceptance requires iOS/tvOS 13+.

Apple platforms use separate binaries. `lipo` combines architectures of the same
platform; `xcodebuild -create-xcframework` combines platforms. No macOS dylib is
loaded into an iOS or Catalyst app.

Install Xcode, Rust through rustup, and CMake. Accept the Xcode license and select
the full Xcode developer directory before building. The scripts use `xcrun` to
find each SDK and `rustup` to install the corresponding Rust standard libraries.

```sh
# macOS universal dylib only (or pass x64 / arm64)
./scripts/macos/build.sh universal
python3 scripts/macos/verify.py

# All Apple architectures and one XCFramework with all six platform slices
./scripts/apple/build.sh all
python3 scripts/apple/verify.py --link
```

For targeted iteration, `build.sh ios`, `build.sh maccatalyst`, `build.sh macos`, and `build.sh tvos`
build only that platform (tvOS preserves existing non-tvOS slices). Each invocation replaces the generated XCFramework;
run `all` before packaging a complete Apple distribution. iPadOS uses the iOS slices; it does not need a separate library. Generated binaries stay
local under `runtimes/`, `Frameworks/` and `quiche-bindgen/target/`.

The macOS universal dylib is placed under both standard .NET RIDs, `osx-x64` and
`osx-arm64`, and under `osx-universal` for manual deployment. The resolver also
accepts the latter location. `osx-universal` is a manual directory convention,
not a .NET runtime identifier. A universal native library does not make a .NET
application universal: build/package the application's managed runtime for each
architecture as appropriate.

## .NET integration

The managed wrapper targets `net7.0`, `net8.0`, `net10.0`, `net10.0-macos`,
`net10.0-ios`, `net10.0-maccatalyst`, and `net10.0-tvos` (plus Windows and Android). Mobile Apple
assemblies import the statically linked symbols through `__Internal`. NuGet
consumers receive the XCFramework NativeReference through the
`Cloudflare.QuicheNet.NativeAssets.iOS` package's `buildTransitive` targets.
There is no runtime `NativeLibrary.Load` on iOS/Catalyst/tvOS.

`EnableAppleLegacyTargets=true` selects the .NET 8 mobile profiles (iOS/tvOS
12.0+, Catalyst 13.1+); `EnableAppleLegacy7Targets=true` selects .NET 7 mobile
profiles (iOS/tvOS 10.0+, Catalyst 13.1+). Use the corresponding installed SDK and
workloads. These profiles share the same native assets; app/runtime and
architecture minima still apply.

Apple SDK 16/17 tooling used by .NET 7/8 selects the matching `.a` from the
XCFramework automatically by application RID. This avoids an older resolver
misclassifying a static slice as a framework. Modern .NET 10 tooling receives the
XCFramework NativeReference directly; all six platform slices stay in the bundle.
A .NET 7 Catalyst app was linked with its legacy packs on Xcode 26 using
`-p:_UseClassicLinker=false -p:SelfContained=true`. Those options select a
compatible linker/runtime packaging mode and do not lower deployment targets.


When consuming projects directly from this checkout, import the native targets
in the **application** project as well as referencing the managed project:

```xml
<Import Project="../Cloudflare.QuicheNet/Cloudflare.QuicheNet.NativeAssets.iOS/buildTransitive/Cloudflare.QuicheNet.NativeAssets.iOS.targets" />
```

For macOS, copy the dylib to `runtimes/osx-<architecture>/native/` next to the app.
The native assets project copies its runtime content for normal
project builds after the native build has completed.

Native builds do not overwrite `Cloudflare.QuicheNet/NativeMethods.g.cs`. That
file contains portable ABI declarations plus Apple `__Internal` selection and
is checked in. Rust's generated, platform-specific C# reference is written to
Cargo's `OUT_DIR` for inspection. Keep it separate from the portable declarations.

## Validation

```sh
# Run from the repository root; BuildNativeAssets=false skips native project
# evaluation so a managed build does not require unrelated mobile workloads.
dotnet build Cloudflare.QuicheNet/Cloudflare.QuicheNet.csproj -p:TargetFrameworks=net10.0 -p:BuildNativeAssets=false
dotnet build Cloudflare.QuicheNet.Tests -p:TargetFrameworks=net10.0 -p:BuildNativeAssets=false
dotnet Cloudflare.QuicheNet.Tests/bin/Debug/net10.0/Cloudflare.QuicheNet.Tests.dll

# Set osx-arm64 on an Apple Silicon runner.
QUICHE_NATIVE_DIR="$PWD/Cloudflare.QuicheNet.NativeAssets.macOS/runtimes/osx-x64/native" \
  dotnet Cloudflare.QuicheNet.Tests/bin/Debug/net10.0/Cloudflare.QuicheNet.Tests.dll --native
```

The tests cover native ABI layouts, platform mapping, the deadline scheduler,
queue completion, anti-replay, header byte preservation, native HTTP/3 creation,
and QUIC loopback stream/FIN/datagram/cancellation behavior. Foreign architecture
slices are inspected with `lipo`, `nm`, and Mach-O load commands from every native Mach-O object in
wrapper, quiche, BoringSSL and Rust standard-library archives; executing them requires a corresponding
Mac, simulator or device. Building an XCFramework does not certify a device run.

Packaging remains a local build step. No package publication is required.

## BoringSSL and the Intel tvOS simulator

The pinned `boring-sys` 4.22.0 build script does not recognize Catalyst or tvOS.
`prepare-boringssl.py` creates separate Catalyst and tvOS copies under Cargo's
ignored `target/` directory
and adds only the missing Apple SDK/compiler-target dispatch. Cargo selects that
copy explicitly for Catalyst and tvOS. The Cargo registry and BoringSSL crypto/TLS
sources are left intact. Version changes require reviewing the patch.

Rust distributes stable standard libraries for tvOS devices and ARM64 simulators,
but the Intel tvOS simulator is a tier-3 target. The default complete build installs
nightly with `rust-src` and uses `-Zbuild-std` for that slice. Set
`QUICHE_TVOS_INTEL=0` for an ARM64-only tvOS simulator during local iteration; that
partial bundle intentionally does not pass the full universal verifier.

`verify.py --link` also links a C smoke executable against every platform/architecture with force-load, catching missing system dependencies without executing foreign code.

Static archives are prepared in the ignored `quiche-bindgen/target/apple/thin`
staging directory before packaging. This removes byte-identical native objects
embedded through both Rust dependency paths, regenerates the archive index and
strips debug information with `strip -S`. Every native public symbol is checked against the original archive after preparation.
Cargo's original archives are preserved. This preparation is necessary for `ForceLoad` consumers.

Deployment targets are chosen per architecture from the installed Rust target
defaults and confirmed against every Mach-O object in the packaged archive.
New or incompatible builds include the minimum in Rust's compilation flags so
Cargo fingerprints it. Existing archives already validated at that minimum keep
their previous flags and remain reusable; ordinary Cargo source checks still run.
Before reusing an older build, `ensure-deployment.sh` checks its object load
commands and cleans that target if its objects require a newer OS. Load commands
are never rewritten to claim older compatibility.

## Runtime checks on an older Mac

Load-command inspection and linking at a deployment target do not constitute
running on that OS. `verify.py --link` creates standalone C probes in
`quiche-bindgen/target/apple/smoke/`. They load native code, check QUIC v1 and
allocate/free QUIC/TLS and HTTP/3 configurations with TLS ALPN 100 times.
They do not perform a network handshake.

`scripts/macos/build-runtime-smoke.sh` additionally builds probes against the
actual universal dylib and copies it beside them. The runner executes both static
and dynamic probes when present.

Copy that smoke directory and `scripts/macos/run-smoke.sh` to the destination Mac,
then pass the expected OS version explicitly, for example:

```sh
bash run-smoke.sh ./smoke 10.12
```

The runner prints the actual OS and architecture and fails if the OS does not
match the requested version prefix. Running it on a current Mac cannot establish
compatibility with an older release. iOS/tvOS execution requires the matching
device or simulator runtime; simulator probes can be launched with `simctl spawn`
(or through a test app). A cross-linked executable alone is not a runtime test.
No older-runtime result is claimed without execution there.

The final 64-bit bundle passed all ten native link probes. The C smoke also ran
on macOS 15.8.1 Intel (static archive and universal dylib), Catalyst on that host,
and the installed iOS 26.3 Intel simulator. These checks cover version lookup and
100 QUIC/TLS/HTTP3 configuration iterations, rather than a network handshake or
execution on the older deployment releases.

Native build helpers also hold an OS file lock across deployment checks, cache
cleanup and Cargo. This lock is released automatically when its processes exit;
there is no stale lock directory to remove. Separate invocations using these
helpers can wait safely without deleting each other's shared Cargo artifacts.

## Optional 32-bit iOS investigation

The shipped bundle contains ARM64 and Intel x86_64 assets. iOS 12+ runs only
64-bit apps; Apple states that iOS 10 was the last release that ran 32-bit apps
and [Xcode 14 removed 32-bit app builds](https://developer.apple.com/news/upcoming-requirements/?id=06062022a).

The installed Rust compiler still recognizes `armv7s-apple-ios` and
`i386-apple-ios` as tier-3 targets with deployment minimum 10.0, without a
prebuilt standard library. `armv7-apple-ios` was
[removed in Rust 1.74](https://blog.rust-lang.org/2023/09/25/Increasing-Apple-Version-Requirements/).
This quiche revision requires Rust 1.88+, so using an old Rust release is not a
solution for that removed target.

No older Xcode/iOS SDK was found under `/Users/lamerma/Projects` during this
build. The selected iOS 26.2 SDK's `libSystem.tbd` contains only ARM64/ARM64e;
even an empty ARMv7s C program cannot link against it. A separate 32-bit build
would first need an older Apple SDK with ARMv7s/i386 system stubs, rebuilt Rust
std, BoringSSL target dispatch and measured Darwin32 ABI layouts. It also needs
an iOS 10 device or matching older simulator environment for runtime testing.
The 64-bit `all` build does not claim or fabricate these unavailable assets.

## Build provenance

After completing the native build, run `python3 scripts/apple/build-info.py` to
write `quiche-bindgen/target/apple/build-info.json`. It snapshots the existing
Cargo lock (including exact package versions/checksums), native input hashes,
patched BoringSSL build-script hashes, Rust/std versions and hashes, Xcode and
SDK versions. It never invokes Cargo or rewrites the lock. Keep that snapshot
beside copied binaries and their file hashes.

The wrapper's local `Cargo.lock` is ignored; fresh dependency resolution is
therefore subject to semver. The Apple preparer explicitly requires
`boring-sys 4.22.0` and fails safely on another version until its dispatch patch is
reviewed. The snapshot records what produced these binaries; it does not promise
that a later unlocked build resolves the same dependencies or is byte-identical.
