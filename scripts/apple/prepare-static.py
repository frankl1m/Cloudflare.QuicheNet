#!/usr/bin/env python3
"""Prepare an Apple staticlib without changing Cargo's original archive.

Rust can embed identical native BoringSSL objects through two dependency paths.
Apple's force_load linker rejects their duplicate symbols. Retain one copy of
identical object bytes, regenerate the index and remove debug information only.
"""
import hashlib
from pathlib import Path
import subprocess
import sys

source, destination = map(Path, sys.argv[1:])
data = source.read_bytes()
# Reuse only a byte-verified result made by this exact preparer.
stamp = destination.with_suffix('.a.sha256')
signature = hashlib.sha256(data + Path(__file__).read_bytes()).hexdigest()
if destination.exists() and stamp.exists():
    expected = signature + ':' + hashlib.sha256(destination.read_bytes()).hexdigest()
    if stamp.read_text() == expected:
        print(f'{destination}: prepared archive is current')
        raise SystemExit(0)
if data[:8] != b'!<arch>\n':
    raise SystemExit(f'Not a thin ar archive: {source}')
records, seen, offset, removed = [], set(), 8, 0
while offset < len(data):
    header = data[offset:offset + 60]
    if len(header) != 60 or header[58:] != b'`\n':
        raise SystemExit(f'Invalid ar header at {offset}: {source}')
    size = int(header[48:58])
    end = offset + 60 + size
    payload = data[offset + 60:end]
    name = header[:16].decode('ascii').strip()
    if name.startswith('#1/'):
        length = int(name[3:])
        name = payload[:length].rstrip(b'\0').decode('utf-8')
        payload = payload[length:]
    if name.startswith('__.SYMDEF') or name in ('/', '/SYM64/'):
        pass  # ranlib regenerates the symbol table after filtering.
    elif name == '//':
        records.append(data[offset:end + (size % 2)])
    else:
        digest = hashlib.sha256(payload).digest()
        if digest in seen:
            removed += 1
        else:
            seen.add(digest)
            records.append(data[offset:end + (size % 2)])
    offset = end + (size % 2)
if offset != len(data):
    raise SystemExit(f'Truncated archive: {source}')
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes(b'!<arch>\n' + b''.join(records))
subprocess.run(['xcrun', 'ranlib', str(destination)], check=True)
subprocess.run(['xcrun', 'strip', '-S', str(destination)], check=True)
subprocess.run(['xcrun', 'ranlib', str(destination)], check=True)
def public_symbols(path):
    result = subprocess.check_output(['xcrun', 'nm', '--no-llvm-bc', '-gU', str(path)], text=True, stderr=subprocess.DEVNULL)
    return {parts[-1] for line in result.splitlines() if len(parts := line.split()) >= 3 and parts[1].isalpha()}

missing = public_symbols(source) - public_symbols(destination)
if missing:
    raise SystemExit(f'Archive preparation lost public symbols: {sorted(missing)}')
stamp.write_text(signature + ':' + hashlib.sha256(destination.read_bytes()).hexdigest())
print(f'{destination}: removed {removed} identical objects; {len(data)} -> {destination.stat().st_size} bytes')
