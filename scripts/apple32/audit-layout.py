#!/usr/bin/env python3
"""Read rustc's layout of quiche's real private FFI types for each Apple32 target."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
staging = root / 'quiche-bindgen/target/apple32'
expected = {
    'Stats': (172, {'sent_bytes': 20, 'bytes_in_flight_duration_msec': 160}),
    'PathStats': (396, {'rtt': 300, 'delivery_rate': 372}),
    'SendInfo': (272, {'at': 264}),
}

def parse_layout(text):
    found, current, offset = {}, None, 0
    for line in text.splitlines():
        header = re.search(r'print-type-size type: `(?:quiche::)?ffi::(Stats|PathStats|SendInfo)`: (\d+) bytes, alignment: (\d+) bytes', line)
        if line.startswith('print-type-size type:'):
            current = None
        if header:
            name, size, alignment = header.groups()
            current, offset = name, 0
            found[name] = {'size': int(size), 'alignment': int(alignment), 'offsets': {}}
        elif current:
            field = re.search(r'print-type-size\s+field `\.([^`]+)`: (\d+) bytes', line)
            padding = re.search(r'print-type-size\s+(?:end )?padding: (\d+) bytes', line)
            if field:
                name, size = field.groups()
                found[current]['offsets'][name] = offset
                offset += int(size)
            elif padding:
                offset += int(padding.group(1))
    for name, (size, offsets) in expected.items():
        actual = found.get(name)
        if actual is None or actual['size'] != size or actual['alignment'] != 4:
            raise ValueError(f'{name}: unexpected Rust FFI layout: {actual}')
        for field, offset in offsets.items():
            if actual['offsets'].get(field) != offset:
                raise ValueError(f'{name}.{field}: unexpected Rust offset {actual["offsets"].get(field)}')
    return found

if __name__ == '__main__':
    with (staging / 'native-build.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        combined_path = staging / 'build-info.json'
        combined = json.loads(combined_path.read_text())
        for arch in ['armv7s', 'i386']:
            artifact = staging / 'artifacts' / arch
            snapshot = artifact / 'build-info.json'
            record = json.loads(snapshot.read_text())
            env = os.environ.copy()
            developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip())
            xcode = Path(record['driver_arguments'][0]).resolve()
            legacy_developer = xcode / 'Contents/Developer' if xcode.suffix == '.app' else xcode
            cpp_headers = legacy_developer / 'Toolchains/XcodeDefault.xctoolchain/usr/include/c++/v1'
            env.update({'DEVELOPER_DIR': str(developer), 'SDKROOT': record['sdk']['path'],
                        'LIBCLANG_PATH': str(developer / 'Toolchains/XcodeDefault.xctoolchain/usr/lib'),
                        'CC': str(developer / 'Toolchains/XcodeDefault.xctoolchain/usr/bin/clang'),
                        'CXX': str(developer / 'Toolchains/XcodeDefault.xctoolchain/usr/bin/clang++'),
                        'CXXFLAGS': '-isystem ' + json.dumps(str(cpp_headers)),
                        'CARGO_TARGET_DIR': str(staging / 'cargo'), 'CARGO_BUILD_JOBS': '4',
                        'IPHONEOS_DEPLOYMENT_TARGET': '10.0',
                        'BINDGEN_EXTRA_CLANG_ARGS': '-isysroot ' + json.dumps(record['sdk']['path']),
                        'CARGO_ENCODED_RUSTFLAGS': '\x1f'.join(record['rustflags'])})
            env.pop('RUSTFLAGS', None)
            patch = 'patch.crates-io.boring-sys.path=' + json.dumps(str(staging / 'boring-sys'))
            command = ['cargo', '+nightly', '--config', patch, 'rustc', '--manifest-path', str(staging / 'workspace/Cargo.toml'),
                       '-Zbuild-std=std,panic_abort', '--release', '--target', record['target'], '-p', 'quiche', '--lib',
                       '--verbose', '--', '-Zprint-type-sizes']
            log = artifact / 'rust-ffi-layout.log'
            cargo_log = artifact / 'rust-ffi-layout.cargo.log'
            with cargo_log.open('w') as stream:
                subprocess.run(command, env=env, stdout=stream, stderr=subprocess.STDOUT, check=True, pass_fds=(lock.fileno(),))
            output = cargo_log.read_text()
            if re.search(r'print-type-size type: `(?:quiche::)?ffi::Stats`:', output):
                log.write_text(output)
            elif not (re.search(r'^\s*Fresh quiche v', output, re.M) and log.exists()
                      and hashlib.sha256(log.read_bytes()).hexdigest() == record.get('rust_ffi_layout', {}).get('log_sha256')):
                raise SystemExit('No fresh layout output or byte-verified cached layout proof')
            result = parse_layout(log.read_text())
            record['rust_ffi_layout'] = {'types': result, 'command': command,
                'log_sha256': hashlib.sha256(log.read_bytes()).hexdigest()}
            # Record the C++ include inputs also for builds started before this metadata field was added.
            record['cxxflags'] = env['CXXFLAGS']
            record['libcxx_config_sha256'] = hashlib.sha256((cpp_headers / '__config').read_bytes()).hexdigest()
            snapshot.write_text(json.dumps(record, indent=2) + '\n')
            combined['architectures'][arch] = record
            combined_path.write_text(json.dumps(combined, indent=2) + '\n')
            print(f'PASS real Rust FFI layout: {arch}: Stats172, PathStats396, SendInfo272; alignment4', flush=True)
