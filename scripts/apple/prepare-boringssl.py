#!/usr/bin/env python3
"""Prepare boring-sys 4.22's Apple build dispatch without changing Cargo's cache.

Only the build script changes. BoringSSL's crypto/TLS sources remain untouched.
The copy lives in Cargo's ignored target directory and is selected with --config.
"""
import json
import shutil
import subprocess
import sys
from pathlib import Path
root = Path(__file__).resolve().parents[2]
metadata = json.loads(subprocess.check_output(['cargo', 'metadata', '--format-version', '1', '--manifest-path', str(root / 'quiche-bindgen/Cargo.toml')], text=True))
package = next(p for p in metadata['packages'] if p['name'] == 'boring-sys')
if package['version'] != '4.22.0':
    raise SystemExit('Review the Apple build patch before using a new boring-sys version')
source = Path(package['manifest_path']).parent
tvos = len(sys.argv) > 1 and sys.argv[1] == 'tvos'
output = root / ('quiche-bindgen/target/boring-sys-apple-tvos' if tvos else 'quiche-bindgen/target/boring-sys-apple')
if source.resolve() == output.resolve():
    raise SystemExit('Run this preparer without the boring-sys Cargo patch override')
if not output.exists():
    shutil.copytree(source, output)
p = output / 'build/main.rs'
text = (source / 'build/main.rs').read_text()
anchor = 'const CMAKE_PARAMS_APPLE: &[(&str, &[(&str, &str)])] = &['
if anchor not in text:
    raise SystemExit('boring-sys build script changed: Apple patch anchor not found')
entries = []
for target, arch, sdk, clang, system, minimum in [
    ('aarch64-apple-ios-macabi', 'arm64', 'macosx', 'arm64-apple-ios13.1-macabi', 'Darwin', ''),
    ('x86_64-apple-ios-macabi', 'x86_64', 'macosx', 'x86_64-apple-ios13.1-macabi', 'Darwin', ''),
    ('aarch64-apple-tvos', 'arm64', 'appletvos', 'arm64-apple-tvos10.0', 'tvOS', '10.0'),
    ('aarch64-apple-tvos-sim', 'arm64', 'appletvsimulator', 'arm64-apple-tvos14.0-simulator', 'tvOS', '14.0'),
    ('x86_64-apple-tvos', 'x86_64', 'appletvsimulator', 'x86_64-apple-tvos10.0-simulator', 'tvOS', '10.0'),
]:
    params = [('CMAKE_OSX_ARCHITECTURES', arch), ('CMAKE_OSX_SYSROOT', sdk),
              ('CMAKE_SYSTEM_NAME', system), ('CMAKE_OSX_DEPLOYMENT_TARGET', minimum)]
    params += [(f'CMAKE_{language}_COMPILER_TARGET', clang) for language in ['C', 'CXX', 'ASM']]
    entries.append(f'("{target}", &[{", ".join(f"({json.dumps(k)}, {json.dumps(v)})" for k, v in params)}]),')
if not tvos:
    entries = [entry.replace('tvos10.0', 'tvos13.0').replace('tvos14.0', 'tvos13.0').replace('\"10.0\"', '\"13.0\"').replace('\"14.0\"', '\"13.0\"') for entry in entries]
text = text.replace(anchor, anchor + '\n' + '\n'.join(entries), 1)
text = text.replace('"macos" | "ios" =>', '"macos" | "ios" | "tvos" =>')
text = text.replace('"ios" | "macos" =>', '"ios" | "macos" | "tvos" =>')
text = text.replace('        "ios" => {', '        "ios" | "tvos" => {')
text = text.replace('if config.target_arch == "x86_64" {\n                "-target x86_64-apple-ios-simulator"', 'if config.target == "x86_64-apple-ios" {\n                "-target x86_64-apple-ios-simulator"')
if tvos:
    runtime_anchor = '\"macos\" | \"ios\" | \"freebsd\" =>'
    if runtime_anchor not in text:
        raise SystemExit('boring-sys C++ runtime dispatch changed: review the tvOS patch')
    text = text.replace(runtime_anchor, '\"macos\" | \"ios\" | \"tvos\" | \"freebsd\" =>', 1)
if not p.exists() or p.read_text() != text:
    p.write_text(text)
print(output)
