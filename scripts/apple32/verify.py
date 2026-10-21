#!/usr/bin/env python3
"""Require all managed exports and a real iOS10 link from an Apple32 archive."""
from pathlib import Path
import re
import subprocess
import sys
from deployment import check, check_object

root = Path(__file__).resolve().parents[2]
library, arch, sdk, clang = sys.argv[1:]
print('Native objects validated:', check(library, arch))
subprocess.run(['lipo', library, '-verify_arch', arch], check=True)
required = {'_' + name for source in (root / 'Cloudflare.QuicheNet').glob('*.cs')
            for name in re.findall(r'EntryPoint = "(_quiche_[^"]+)"', source.read_text())}
if len(required) < 170:
    raise SystemExit('Managed export list unexpectedly incomplete')
symbols = subprocess.check_output(['xcrun', 'nm', '--no-llvm-bc', '-gU', library], text=True)
exported = {parts[-1] for line in symbols.splitlines() if len(parts := line.split()) >= 3}
missing = required - exported
if missing:
    raise SystemExit(f'Missing managed exports: {sorted(missing)}')
triple = arch + '-apple-ios10.0' + ('-simulator' if arch == 'i386' else '')
output = Path(library).parent / 'quiche-smoke'
subprocess.run([clang, '-target', triple, '-isysroot', sdk,
                str(root / 'scripts/apple/native-smoke.c'), '-Wl,-force_load,' + str(Path(library).resolve()),
                '-framework', 'Security', '-framework', 'SystemConfiguration', '-lc++', '-liconv', '-lresolv',
                '-o', str(output)], check=True)
# Check final executable as well: linker output must remain 32-bit and iOS10.
subprocess.run(['lipo', str(output), '-verify_arch', arch], check=True)
if not check_object(output.read_bytes(), output.name, arch):
    raise SystemExit('Final executable is not Apple32 Mach-O')
print(f'PASS Apple32 {arch}: {len(required)} exports and native force_load link; runtime untested')
