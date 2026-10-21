#!/usr/bin/env python3
"""Inspect the universal macOS dylib without executing its ARM64 slice."""
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
library = Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'Cloudflare.QuicheNet.NativeAssets.macOS/runtimes/osx-universal/native/libquiche_bindgen.dylib'
architectures = set(subprocess.check_output(['lipo', str(library), '-archs'], text=True).split())
if architectures != {'x86_64', 'arm64'}:
    raise SystemExit(f'Expected universal macOS dylib, got {architectures}')
required = {'_' + symbol for source in (root / 'Cloudflare.QuicheNet').glob('*.cs') for symbol in re.findall(r'EntryPoint = "(_quiche_[^"]+)"', source.read_text())}
if len(required) < 170:
    raise SystemExit('Managed entry-point list is unexpectedly incomplete')
for arch, minimum in [('x86_64', (10, 12, 0)), ('arm64', (11, 0, 0))]:
    commands = subprocess.check_output(['xcrun', 'vtool', '-arch', arch, '-show-build', str(library)], text=True)
    match = re.search(r'platform\s+MACOS\s+minos\s+(\d+\.\d+(?:\.\d+)?)', commands)
    if match is None:
        match = re.search(r'LC_VERSION_MIN_MACOSX.*?version\s+(\d+\.\d+(?:\.\d+)?)', commands, re.S)
    actual = tuple(map(int, match.group(1).split('.'))) if match else ()
    actual += (0,) * (3 - len(actual))
    if match is None or actual > minimum:
        raise SystemExit(f'{arch}: expected macOS <= {minimum}: {commands}')
    symbols = subprocess.check_output(['xcrun', 'nm', '--no-llvm-bc', '-arch', arch, '-gU', str(library)], text=True)
    missing = required - {line.split()[-1] for line in symbols.splitlines() if line.split()}
    if missing:
        raise SystemExit(f'{arch}: missing {sorted(missing)}')
    print(f'PASS macOS {arch}: minimum {match.group(1)}; {len(required)} managed exports')
subprocess.run(['codesign', '--verify', str(library)], check=True)
