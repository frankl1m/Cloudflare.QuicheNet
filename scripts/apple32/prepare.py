#!/usr/bin/env python3
"""Prepare an independent 32-bit Apple workspace; never modify 64-bit outputs."""
import json
from pathlib import Path
import shutil
import sys

if len(sys.argv) != 3:
    raise SystemExit('Usage: prepare.py <iPhoneOS.sdk> <iPhoneSimulator.sdk>')
device_sdk, simulator_sdk = [str(Path(value).resolve()) for value in sys.argv[1:]]
root = Path(__file__).resolve().parents[2]
staging = root / 'quiche-bindgen/target/apple32'
workspace = staging / 'workspace'
source = root / 'quiche-bindgen'
workspace.mkdir(parents=True, exist_ok=True)
for name in ['build.rs', 'src/lib.rs', 'include/quiche.h']:
    destination = workspace / name
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = (source / name).read_bytes()
    if not destination.exists() or destination.read_bytes() != data:
        destination.write_bytes(data)
manifest = (source / 'Cargo.toml').read_text()
old = 'path = "../quiche/quiche"'
if manifest.count(old) != 1:
    raise SystemExit('Review the isolated quiche path before preparing Apple32')
manifest = manifest.replace(old, 'path = ' + json.dumps(str(root / 'quiche/quiche')))
manifest += '\n[workspace]\n'
(workspace / 'Cargo.toml').write_text(manifest)
if not (workspace / 'Cargo.lock').exists():
    shutil.copy2(source / 'Cargo.lock', workspace / 'Cargo.lock')

# Start from the immutable registry source, rather than modifying either Apple64 patch.
candidates = sorted((Path.home() / '.cargo/registry/src').glob('*/boring-sys-4.22.0'))
if len(candidates) != 1:
    raise SystemExit('Expected exactly one local registry source for boring-sys 4.22.0')
upstream = candidates[0]
patched = staging / 'boring-sys'
if not patched.exists():
    shutil.copytree(upstream, patched)
text = (upstream / 'build/main.rs').read_text()
anchor = 'const CMAKE_PARAMS_APPLE: &[(&str, &[(&str, &str)])] = &['
if text.count(anchor) != 1:
    raise SystemExit('Review the boring-sys Apple32 dispatch anchor')
entries = []
for target, arch, sdk, triple in [
    ('armv7s-apple-ios', 'armv7s', device_sdk, 'armv7s-apple-ios10.0'),
    ('i386-apple-ios', 'i386', simulator_sdk, 'i386-apple-ios10.0-simulator'),
]:
    values = [('CMAKE_OSX_ARCHITECTURES', arch), ('CMAKE_OSX_SYSROOT', sdk),
              ('CMAKE_OSX_DEPLOYMENT_TARGET', '10.0')]
    values += [(f'CMAKE_{language}_COMPILER_TARGET', triple) for language in ['C', 'CXX', 'ASM']]
    entries.append(f'({json.dumps(target)}, &[{", ".join(f"({json.dumps(k)}, {json.dumps(v)})" for k, v in values)}]),')
text = text.replace(anchor, anchor + '\n' + '\n'.join(entries), 1)
text = text.replace('if config.target_arch == "x86_64" {\n                "-target x86_64-apple-ios-simulator"', 'if config.target == "x86_64-apple-ios" {\n                "-target x86_64-apple-ios-simulator"')
# Bitcode bundling is retired; current ld rejects the old iOS32 bitcode flags.
text = text.replace('let bitcode_cflag = \"-fembed-bitcode\";', 'let bitcode_cflag = \"\";')
build = patched / 'build/main.rs'
if build.read_text() != text:
    build.write_text(text)
print(workspace)
