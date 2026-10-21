#!/usr/bin/env python3
"""Validate the complete Apple XCFramework without executing foreign slices."""
import argparse
import importlib.util
import plistlib
import re
import tempfile
import subprocess
import sys
from pathlib import Path
from deployment import check, inspect_archive, version_number

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('framework', nargs='?', type=Path, default=root / 'Cloudflare.QuicheNet.NativeAssets.iOS/Frameworks/quiche_bindgen.xcframework')
parser.add_argument('--allow-partial', action='store_true', help='Verify the included slices without requiring every Apple platform')
parser.add_argument('--link', action='store_true', help='Link a native smoke executable for every platform and architecture')
parser.add_argument('--include-ios32', action='store_true', help='Require ARMv7s device and i386 simulator slices as well')
parser.add_argument('--legacy-xcode', type=Path, help='Extracted older Xcode for linking iOS32 probes')
options = parser.parse_args()
check32 = None
framework = options.framework
expected = {
    ('macos', ''): {'x86_64', 'arm64'},
    ('ios', ''): {'arm64'},
    ('ios', 'simulator'): {'x86_64', 'arm64'},
    ('ios', 'maccatalyst'): {'x86_64', 'arm64'},
    ('tvos', ''): {'arm64'},
    ('tvos', 'simulator'): {'x86_64', 'arm64'},
}
with (framework / 'Info.plist').open('rb') as stream:
    slices = plistlib.load(stream)['AvailableLibraries']
# Packs need no extra flag: accept either complete matrix, never half of iOS32.
if options.include_ios32 or any(arch in ('armv7s', 'i386') for item in slices for arch in item['SupportedArchitectures']):
    options.include_ios32 = True
    expected[('ios', '')].add('armv7s')
    expected[('ios', 'simulator')].add('i386')
    specification = importlib.util.spec_from_file_location('quiche_deployment32', root / 'scripts/apple32/deployment.py')
    module32 = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module32)
    check32 = module32.check
    if options.link and options.legacy_xcode is None:
        parser.error('linking iOS32 requires --legacy-xcode')
platform_names = {
    ('macos', ''): 'MACOS', ('ios', ''): 'IOS',
    ('ios', 'simulator'): 'IOSSIMULATOR', ('ios', 'maccatalyst'): 'MACCATALYST',
    ('tvos', ''): 'TVOS', ('tvos', 'simulator'): 'TVOSSIMULATOR',
}

def minimum_for(key, arch):
    if arch == 'arm64':
        return '11.0' if key == ('macos', '') else ('10.0' if key[1] == '' else '14.0')
    return '10.12' if key[0] == 'macos' else ('13.1' if key[1] == 'maccatalyst' else '10.0')

def verify_object_platforms(library, arch, key):
    with tempfile.TemporaryDirectory(prefix='quiche-apple-verify-') as directory:
        thin = Path(directory) / 'thin.a'
        archs = subprocess.check_output(['lipo', str(library), '-archs'], text=True).split()
        if len(archs) > 1:
            subprocess.run(['lipo', str(library), '-thin', arch, '-output', str(thin)], check=True)
        else:
            thin = library
        try:
            count = check32(thin, arch) if arch in ('armv7s', 'i386') else check(thin, arch, platform_names[key], minimum_for(key, arch))
        except ValueError as error:
            raise SystemExit(f'{library}/{arch}: {error}')
        names = [name for name, _ in inspect_archive(thin, arch)]
        required = ['quiche_bindgen.', 'quiche.', 'std-']
        if any(not any(name.startswith(prefix) for name in names) for prefix in required) or not any(name.endswith('ssl_lib.cc.o') for name in names):
            raise SystemExit(f'{library}/{arch}: missing wrapper, QUIC, BoringSSL or Rust std object')
        print(f'  {arch}: checked {count} object deployment commands, minimum <= {minimum_for(key, arch)}')

def verify_native_link(library, arch, key):
    minimum = minimum_for(key, arch)
    sdk, target = {
        ('macos', ''): ('macosx', f'{arch}-apple-macos{minimum}'),
        ('ios', ''): ('iphoneos', f'{arch}-apple-ios{minimum}'),
        ('ios', 'simulator'): ('iphonesimulator', f'{arch}-apple-ios{minimum}-simulator'),
        ('ios', 'maccatalyst'): ('macosx', f'{arch}-apple-ios{minimum}-macabi'),
        ('tvos', ''): ('appletvos', f'{arch}-apple-tvos{minimum}'),
        ('tvos', 'simulator'): ('appletvsimulator', f'{arch}-apple-tvos{minimum}-simulator'),
    }[key]
    # clang spells the ARM architecture arm64, as lipo does.
    if arch in ('armv7s', 'i386'):
        developer = options.legacy_xcode.resolve()
        if developer.suffix == '.app':
            developer = developer / 'Contents/Developer'
        platform = 'iPhoneSimulator' if arch == 'i386' else 'iPhoneOS'
        candidates = sorted({path.resolve() for path in (developer / f'Platforms/{platform}.platform/Developer/SDKs').glob('*.sdk') if path.is_dir()})
        if len(candidates) != 1:
            raise SystemExit(f'Expected one legacy SDK for {platform}: {candidates}')
        sysroot = str(candidates[0])
        compiler = [subprocess.check_output(['xcrun', '--find', 'clang'], text=True).strip()]
    else:
        sysroot = subprocess.check_output(['xcrun', '--sdk', sdk, '--show-sdk-path'], text=True).strip()
        compiler = ['xcrun', '--sdk', sdk, 'clang']
    output = root / 'quiche-bindgen/target/apple/smoke'
    output.mkdir(parents=True, exist_ok=True)
    source = root / 'scripts/apple/native-smoke.c'
    subprocess.run(compiler + ['-target', target, '-isysroot', sysroot,
                    str(source), '-Wl,-force_load,' + str(library.resolve()),
                    '-framework', 'Security', '-framework', 'SystemConfiguration',
                    '-lc++', '-liconv', '-lresolv', '-o', str(output / target)], check=True)
    commands = subprocess.check_output(['xcrun', 'vtool', '-show-build', str(output / target)], text=True)
    found = re.search(r'\bminos\s+(\d+(?:\.\d+){1,2})', commands)
    if found is None:
        found = re.search(r'LC_VERSION_MIN_\w+.*?\bversion\s+(\d+(?:\.\d+){1,2})', commands, re.S)
    if found is None or version_number(found.group(1)) > version_number(minimum):
        raise SystemExit(f'Smoke executable has unexpected deployment target: {commands}')


required_symbols = {'_' + name for source in (root / 'Cloudflare.QuicheNet').glob('*.cs') for name in re.findall(r'EntryPoint = "(_quiche_[^"]+)"', source.read_text())}
if len(required_symbols) < 170:
    raise SystemExit('Managed entry-point list is unexpectedly incomplete')

seen = set()
for item in slices:
    key = (item['SupportedPlatform'], item.get('SupportedPlatformVariant', ''))
    if key not in expected:
        raise SystemExit(f'Unexpected platform: {key}')
    if key in seen:
        raise SystemExit(f'Duplicate platform: {key}')
    seen.add(key)
    architectures = set(item['SupportedArchitectures'])
    if architectures != expected[key] and not (options.allow_partial and architectures and architectures <= expected[key]):
        raise SystemExit(f'{key}: expected {expected[key]}, got {architectures}')
    library = framework / item['LibraryIdentifier'] / item['LibraryPath']
    subprocess.run(['lipo', str(library), '-verify_arch', *sorted(architectures)], check=True)
    # Check the wrapper entry points, not just quiche's own C API. nm prints an
    # extra underscore for Mach-O's C symbols.
    for arch in sorted(architectures):
        verify_object_platforms(library, arch, key)
        if options.link:
            verify_native_link(library, arch, key)
        # Inspect native Mach-O symbols only. Prebuilt Rust std also contains
        # LLVM bitcode from a newer LLVM than Xcode's optional bitcode reader.
        symbols = subprocess.check_output(['xcrun', 'nm', '--no-llvm-bc', '-arch', arch, '-gU', str(library)], text=True)
        exported = {line.split()[-1] for line in symbols.splitlines() if len(line.split()) >= 3}
        for symbol in sorted(required_symbols):
            if symbol not in exported:
                raise SystemExit(f'{key}/{arch}: missing {symbol}')
    print(f'PASS {key}: {", ".join(sorted(architectures))}')
if not options.allow_partial and seen != set(expected):
    raise SystemExit(f'Missing platforms: {set(expected) - seen}')
print(f'Apple XCFramework: {len(seen)} platform slices verified' + (' (partial bundle)' if options.allow_partial else ' (complete bundle)'))
