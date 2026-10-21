#!/usr/bin/env python3
"""Attempt isolated iOS10 ARMv7s/i386 native builds with an extracted older SDK."""
import argparse
import fcntl
import hashlib
import json
import os
import plistlib
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('xcode', type=Path, help='Extracted Xcode.app; does not change xcode-select')
parser.add_argument('--arch', choices=['all', 'armv7s', 'i386'], default='all')
parser.add_argument('--probe-only', action='store_true', help='Check old SDK headers/stubs by compiling and linking C only')
args = parser.parse_args()
developer = args.xcode.resolve()
if developer.suffix == '.app':
    developer = developer / 'Contents/Developer'
modern = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip())
clang = modern / 'Toolchains/XcodeDefault.xctoolchain/usr/bin/clang'
clangxx = clang.with_name('clang++')
if not developer.is_dir() or not clang.is_file():
    raise SystemExit('An extracted Xcode and the current Apple clang are required')
staging = root / 'quiche-bindgen/target/apple32'
staging.mkdir(parents=True, exist_ok=True)

def run(command, **kwargs):
    print('+', ' '.join(map(str, command)), flush=True)
    return subprocess.run(list(map(str, command)), check=True, **kwargs)

def sdk_for(platform):
    directory = developer / f'Platforms/{platform}.platform/Developer/SDKs'
    sdks = sorted({path.resolve() for path in directory.glob('*.sdk') if path.is_dir()})
    if len(sdks) != 1:
        raise SystemExit(f'Expected one SDK in {directory}, found {sdks}')
    return sdks[0]

with (staging / 'native-build.lock').open('a') as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    common = os.environ.copy()
    # Keep host build scripts on the current macOS SDK; target SDKs are absolute.
    common['DEVELOPER_DIR'] = str(modern)
    common['LIBCLANG_PATH'] = str(modern / 'Toolchains/XcodeDefault.xctoolchain/usr/lib')
    common['CARGO_TARGET_DIR'] = str(staging / 'cargo')
    common['CARGO_BUILD_JOBS'] = common.get('CARGO_BUILD_JOBS', '4')
    common['CC'], common['CXX'] = str(clang), str(clangxx)
    cpp_headers = developer / 'Toolchains/XcodeDefault.xctoolchain/usr/include/c++/v1'
    if not (cpp_headers / 'memory').exists():
        raise SystemExit('The extracted Xcode must include its matching libc++ headers')
    common['CXXFLAGS'] = '-isystem ' + json.dumps(str(cpp_headers))
    common['IPHONEOS_DEPLOYMENT_TARGET'] = '10.0'
    for arch in (['armv7s', 'i386'] if args.arch == 'all' else [args.arch]):
        target = arch + '-apple-ios'
        sdk = sdk_for('iPhoneSimulator' if arch == 'i386' else 'iPhoneOS')
        triple = arch + '-apple-ios10.0' + ('-simulator' if arch == 'i386' else '')
        artifact = staging / 'artifacts' / arch
        artifact.mkdir(parents=True, exist_ok=True)
        env = common.copy()
        env['SDKROOT'] = str(sdk)
        env['BINDGEN_EXTRA_CLANG_ARGS'] = '-isysroot ' + json.dumps(str(sdk))
        flags = ['-C', 'linker=' + str(clang), '-C', 'link-arg=-isysroot', '-C', 'link-arg=' + str(sdk),
                 '--cfg=quiche_deployment_target="10.0"']
        env.pop('RUSTFLAGS', None)
        env['CARGO_ENCODED_RUSTFLAGS'] = '\x1f'.join(flags)
        probe = artifact / 'sdk-probe.c'
        probe.write_text('#include <stdint.h>\n#include <stdlib.h>\nint main(void) { return sizeof(void*) != 4 || sizeof(uint64_t) != 8; }\n')
        run([clang, '-target', triple, '-isysroot', sdk, probe, '-lc++', '-o', artifact / 'sdk-probe'], env=env)
        run(['lipo', artifact / 'sdk-probe', '-verify_arch', arch], env=env)
        if args.probe_only:
            continue
        run([sys.executable, root / 'scripts/apple32/prepare.py', sdk_for('iPhoneOS'), sdk_for('iPhoneSimulator')], env=env)
        workspace = staging / 'workspace'
        patch = 'patch.crates-io.boring-sys.path=' + json.dumps(str(staging / 'boring-sys'))
        run(['cargo', '+nightly', '--config', patch, 'build', '--manifest-path', workspace / 'Cargo.toml',
             '-Zbuild-std=std,panic_abort', '--release', '--target', target], env=env, pass_fds=(lock.fileno(),))
        raw = staging / 'cargo' / target / 'release/libquiche_bindgen.a'
        run([sys.executable, root / 'scripts/apple32/deployment.py', raw, arch], env=env)
        library = artifact / 'libquiche_bindgen.a'
        run([sys.executable, root / 'scripts/apple/prepare-static.py', raw, library], env=env)
        run([sys.executable, root / 'scripts/apple32/deployment.py', library, arch], env=env)
        run([sys.executable, root / 'scripts/apple32/verify.py', library, arch, sdk, clang], env=env)
        # Capture this target before the next target rewrites generated bindings.
        def sha(path):
            return hashlib.sha256(path.read_bytes()).hexdigest()
        def read_command(command):
            return subprocess.check_output(list(map(str, command)), env=env, text=True).strip()
        files = [workspace / 'Cargo.toml', workspace / 'build.rs', workspace / 'include/quiche.h']
        files += sorted((workspace / 'src').glob('*.rs'))
        files += sorted((root / 'scripts/apple32').glob('*.py'))
        files += [root / 'quiche/quiche/src/ffi.rs', root / 'quiche/quiche/include/quiche.h']
        settings = sdk / 'SDKSettings.plist'
        sdk_metadata = plistlib.loads(settings.read_bytes()) if settings.exists() else {}
        lock_text = (workspace / 'Cargo.lock').read_text()
        record = {
            'schema_version': 1, 'architecture': arch, 'target': target,
            'scope': 'Isolated Apple32 compile and force_load link; no runtime execution claimed.',
            'quiche_commit': read_command(['git', '-C', root / 'quiche', 'rev-parse', 'HEAD']),
            'driver_arguments': sys.argv[1:], 'rustflags': flags,
            'cxxflags': env['CXXFLAGS'], 'libcxx_config_sha256': sha(cpp_headers / '__config'),
            'deployment_target': '10.0', 'sdk': {'path': str(sdk), 'version': sdk_metadata.get('Version'),
                'settings_sha256': sha(settings) if settings.exists() else None},
            'rustc': read_command(['rustc', '+nightly', '-Vv']),
            'cargo': read_command(['cargo', '+nightly', '-V']),
            'clang': read_command([clang, '--version']),
            'cargo_lock': {'sha256': sha(workspace / 'Cargo.lock'), 'contents': lock_text},
            'boring_sys_4_22_build_script_sha256': sha(staging / 'boring-sys/build/main.rs'),
            'compiled_input_hashes': {str(path.relative_to(root)): sha(path) for path in files},
            'standard_library_hashes': {str(path.relative_to(staging)): sha(path)
                for path in sorted((staging / 'cargo' / target / 'release').rglob('libstd-*.rlib'))},
            'artifacts': {str(path.relative_to(staging)): sha(path) for path in [raw, library, artifact / 'quiche-smoke']},
        }
        (artifact / 'build-info.json').write_text(json.dumps(record, indent=2) + '\n')
        combined = {'schema_version': 1, 'scope': 'Apple32 provenance; separate from the verified Apple64 snapshot.',
                    'architectures': {}}
        for name in ['armv7s', 'i386']:
            snapshot = staging / 'artifacts' / name / 'build-info.json'
            if snapshot.exists():
                combined['architectures'][name] = json.loads(snapshot.read_text())
        (staging / 'build-info.json').write_text(json.dumps(combined, indent=2) + '\n')
        print('Apple32 snapshot:', staging / 'build-info.json', flush=True)
