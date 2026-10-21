#!/usr/bin/env python3
"""Snapshot native build provenance without invoking Cargo or changing its lock."""
import hashlib
import json
from pathlib import Path
import platform
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
output = Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'quiche-bindgen/target/apple/build-info.json'

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def command(*args):
    return subprocess.check_output(args, text=True).strip()

def file_record(path):
    return {'path': str(path.relative_to(root)), 'sha256': sha(path)}

lock = root / 'quiche-bindgen/Cargo.lock'
lock_text = lock.read_text()
if re.search(r'https?://[^/@\s]+:[^/@\s]+@', lock_text):
    raise SystemExit('Refusing to snapshot a lock containing URL credentials')
packages = []
for block in lock_text.split('[[package]]')[1:]:
    package = {}
    for field in ['name', 'version', 'source', 'checksum']:
        match = re.search(r'^' + field + r' = ("[^"\n]*")$', block, re.M)
        if match:
            package[field] = json.loads(match.group(1))
    packages.append(package)

patches = []
for name in ['boring-sys-apple', 'boring-sys-apple-tvos']:
    directory = root / 'quiche-bindgen/target' / name
    build = directory / 'build/main.rs'
    checksum = directory / '.cargo-checksum.json'
    if build.exists():
        record = file_record(build)
        record['upstream_version'] = '4.22.0'
        if checksum.exists():
            record['upstream_crate_checksum'] = json.loads(checksum.read_text()).get('package')
        patches.append(record)

native_inputs = [root / path for path in [
    'quiche-bindgen/Cargo.toml', 'quiche-bindgen/build.rs',
    'quiche-bindgen/.cargo/config.toml', 'quiche-bindgen/src/lib.rs',
    'quiche/quiche/include/quiche.h', 'quiche/quiche/src/ffi.rs',
]]
native_inputs += sorted((root / 'scripts/apple').glob('*.py'))
native_inputs += sorted((root / 'scripts/apple').glob('*.sh'))
native_inputs += [root / 'scripts/apple/native-smoke.c']
native_inputs += sorted((root / 'scripts/macos').glob('*.py'))
native_inputs += sorted((root / 'scripts/macos').glob('*.sh'))

std_libraries = []
for target in [
    'x86_64-apple-darwin', 'aarch64-apple-darwin', 'aarch64-apple-ios',
    'aarch64-apple-ios-sim', 'x86_64-apple-ios', 'aarch64-apple-ios-macabi',
    'x86_64-apple-ios-macabi', 'aarch64-apple-tvos', 'aarch64-apple-tvos-sim',
]:
    directory = Path(command('rustc', '--print', 'target-libdir', '--target', target))
    for library in sorted(directory.glob('libstd-*.rlib')):
        std_libraries.append({'target': target, 'toolchain': 'stable', 'file': library.name, 'sha256': sha(library)})
for library in sorted((root / 'quiche-bindgen/target/x86_64-apple-tvos/release').rglob('libstd-*.rlib')):
    std_libraries.append({'target': 'x86_64-apple-tvos', 'toolchain': 'nightly-build-std', 'file': library.name, 'sha256': sha(library)})
nightly_sysroot = Path(command('rustc', '+nightly', '--print', 'sysroot'))
std_lock = nightly_sysroot / 'lib/rustlib/src/rust/library/Cargo.lock'
if not std_lock.exists():
    std_lock = nightly_sysroot / 'lib/rustlib/src/rust/Cargo.lock'

sdks = []
for sdk in ['macosx', 'iphoneos', 'iphonesimulator', 'appletvos', 'appletvsimulator']:
    directory = Path(command('xcrun', '--sdk', sdk, '--show-sdk-path'))
    settings = next((directory / name for name in ['SDKSettings.json', 'SDKSettings.plist'] if (directory / name).exists()), None)
    sdks.append({'sdk': sdk, 'version': command('xcrun', '--sdk', sdk, '--show-sdk-version'),
                 'directory': directory.name, 'settings_sha256': sha(settings) if settings else None})

result = {
    'schema_version': 1,
    'scope': 'Exact local dependency/toolchain snapshot; does not claim execution on older OS releases.',
    'source': {
        'quiche_commit': command('git', '-C', str(root / 'quiche'), 'rev-parse', 'HEAD'),
        'quiche_dirty': bool(command('git', '-C', str(root / 'quiche'), 'status', '--porcelain')),
        'wrapper_commit_at_snapshot': command('git', '-C', str(root), 'rev-parse', 'HEAD'),
        'wrapper_dirty_at_snapshot': bool(command('git', '-C', str(root), 'status', '--porcelain')),
        'native_input_files': [file_record(path) for path in native_inputs],
    },
    'cargo_lock': {'sha256': sha(lock), 'contents': lock_text, 'packages': packages},
    'boring_sys_patched_build_scripts': patches,
    'rust': {'stable': command('rustc', '-Vv'), 'nightly': command('rustc', '+nightly', '-Vv'),
             'cargo_stable': command('cargo', '-V'), 'cargo_nightly': command('cargo', '+nightly', '-V'),
             'standard_libraries': std_libraries,
             'nightly_std_source_lock_sha256': sha(std_lock) if std_lock.exists() else None},
    'apple': {'xcode': command('xcodebuild', '-version'), 'sdks': sdks},
    'host': {'macos': command('sw_vers', '-productVersion'), 'architecture': platform.machine()},
}
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(result, indent=2) + '\n')
print(output)
