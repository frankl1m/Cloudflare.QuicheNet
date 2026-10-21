#!/usr/bin/env python3
"""Create a candidate six-platform/twelve-architecture XCFramework, preserving Apple64."""
import hashlib
import json
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
staging = root / 'quiche-bindgen/target/apple32'
framework = root / 'Cloudflare.QuicheNet.NativeAssets.iOS/Frameworks/quiche_bindgen.xcframework'
backup = staging / 'backup/quiche_bindgen.xcframework'
info = json.loads((staging / 'build-info.json').read_text())
for arch in ['armv7s', 'i386']:
    record = info['architectures'][arch]
    library = staging / 'artifacts' / arch / 'libquiche_bindgen.a'
    expected = record['artifacts'][str(library.relative_to(staging))]
    if hashlib.sha256(library.read_bytes()).hexdigest() != expected or 'rust_ffi_layout' not in record:
        raise SystemExit(f'{arch}: validated archive hash or real Rust ABI proof missing')
if not backup.exists():
    backup.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(framework, backup)
slices = plistlib.loads((backup / 'Info.plist').read_bytes())['AvailableLibraries']
by_key = {(item['SupportedPlatform'], item.get('SupportedPlatformVariant', '')): item for item in slices}
if len(by_key) != 6 or set(by_key[('ios', '')]['SupportedArchitectures']) != {'arm64'} or set(by_key[('ios', 'simulator')]['SupportedArchitectures']) != {'arm64', 'x86_64'}:
    raise SystemExit('The preserved backup must be the verified complete Apple64 bundle')
args = []
for key, item in by_key.items():
    library = backup / item['LibraryIdentifier'] / item['LibraryPath']
    if key in [('ios', ''), ('ios', 'simulator')]:
        arch = 'armv7s' if key[1] == '' else 'i386'
        destination = staging / 'universal' / ('ios' if arch == 'armv7s' else 'simulator') / 'libquiche_bindgen.a'
        destination.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(['lipo', '-create', str(library), str(staging / 'artifacts' / arch / 'libquiche_bindgen.a'), '-output', str(destination)], check=True)
        subprocess.run(['lipo', str(destination), '-verify_arch', *item['SupportedArchitectures'], arch], check=True)
        library = destination
    args += ['-library', str(library)]
with tempfile.TemporaryDirectory(prefix='xcframework-', dir=staging) as directory:
    output = Path(directory) / 'quiche_bindgen.xcframework'
    subprocess.run(['xcodebuild', '-create-xcframework', *args, '-output', str(output)], check=True)
    candidate = staging / 'quiche_bindgen.xcframework'
    if candidate.exists():
        shutil.rmtree(candidate)
    shutil.move(str(output), candidate)
info['xcframework_candidate'] = {
    'files': {str(path.relative_to(candidate)): hashlib.sha256(path.read_bytes()).hexdigest()
              for path in sorted(candidate.rglob('*')) if path.is_file()},
    'packager_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    'status': 'Packaged candidate; full verifier must pass before installation',
}
(staging / 'build-info.json').write_text(json.dumps(info, indent=2) + '\n')
print('Candidate:', candidate)
print('Apple64 preserved:', backup)
print('Verify candidate with --include-ios32 --legacy-xcode before replacing the installed framework.')
