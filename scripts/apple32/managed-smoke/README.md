# Managed Apple32 link smoke

This small .NET 7 application references the fork's existing managed iOS DLL and
its packaged XCFramework. It exercises static QUIC configuration, TLS ALPN and
HTTP/3 imports if executed. Building it verifies managed/native linking only;
it does not demonstrate execution on an iOS 10 device or simulator.

Prerequisites: the isolated .NET 8 SDK with the historical .NET 7 Apple workload,
a previously built `Cloudflare.QuicheNet/bin/Debug/net7.0-ios/Cloudflare.QuicheNet.dll`,
the verified XCFramework containing ARMv7s and i386, and extracted Xcode 10.3
(SDK 12.4). The current Xcode remains installed and selected for host tools.
The project has no reference to Socks5Lib and does not rebuild native bindings.

Run from the repository root, adjusting these paths to your local installations:

```sh
legacy_xcode=/path/to/Xcode10.3.app
modern_developer=/Applications/Xcode.app/Contents/Developer
legacy_dotnet=/path/to/dotnet8/dotnet
MD_APPLE_SDK_ROOT="$legacy_xcode" "$legacy_dotnet" build \
  scripts/apple32/managed-smoke/QuicheApple32Smoke.csproj -c Release \
  -p:RuntimeIdentifier=ios-arm -p:TargetArchitectures=ARMv7s \
  -p:AppleLegacyXcode="$legacy_xcode" -p:ModernDeveloperRoot="$modern_developer" \
  -p:MtouchLink=SdkOnly -p:MtouchUseLlvm=false -p:MtouchDebug=false -p:DebuggerSupport=false \
  -p:NoDSymUtil=true -p:NoSymbolStrip=true -p:UseSharedCompilation=false -m:1
```

For the Intel simulator, repeat with `RuntimeIdentifier=iossimulator-x86` and
`TargetArchitectures=i386`. Keep separate output directories or finish one build
before starting the other. ARMv7 is not supplied: the `ios-arm` RID must explicitly
select `ARMv7s`.

`MD_APPLE_SDK_ROOT` lets the historical workload discover its compatible Xcode.
The target after `_DetectSdkLocations` then keeps that SDK's target headers and
libraries while selecting `ModernDeveloperRoot` for current host tools. Set
`AppleLegacySdkVersion` to override its default `12.4` only with a matching SDK.
`MtouchLink=SdkOnly` excludes unused SDK APIs that are newer than SDK 12.4.
The command keeps Mono AOT and disables its LLVM backend, debugger support and
optional symbol processing. This avoids obsolete bitcode paths and the debug
runtime's unavailable fd-set symbol; dSYM generation and stripping are outside
this link probe. Signing and provisioning are disabled in the project.

The program accepts `--native-smoke` to return after the import exercise without
starting UIKit. Running the resulting application requires a compatible 32-bit
Apple environment; modern iOS simulators cannot establish that runtime support.
