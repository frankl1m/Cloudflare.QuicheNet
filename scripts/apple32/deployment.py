#!/usr/bin/env python3
"""Audit CPU, platform and deployment commands of every Apple32 native object."""
from pathlib import Path
import struct
import sys

CPU = {'armv7s': (12, 11), 'i386': (7, 3)}

def objects(path):
    data = Path(path).read_bytes()
    if data[:8] != b'!<arch>\n':
        raise ValueError('Expected a thin static archive')
    offset = 8
    while offset < len(data):
        header = data[offset:offset + 60]
        if len(header) != 60 or header[58:] != b'`\n':
            raise ValueError('Invalid archive header')
        size = int(header[48:58])
        payload = data[offset + 60:offset + 60 + size]
        name = header[:16].decode('ascii').strip()
        if name.startswith('#1/'):
            length = int(name[3:])
            name = payload[:length].rstrip(b'\0').decode()
            payload = payload[length:]
        yield name, payload
        offset += 60 + size + size % 2
    if offset != len(data):
        raise ValueError('Truncated static archive')

def check_object(payload, name, arch):
    cpu, subtype = CPU[arch]
    if payload[:4] != b'\xce\xfa\xed\xfe':
        if payload[:4] == b'\xcf\xfa\xed\xfe':
            raise ValueError(f'{name}: unexpected 64-bit native object')
        return False
    actual_cpu, actual_subtype, _, commands = struct.unpack_from('<IIII', payload, 4)
    # Generic ARM/v7 objects are valid inputs for an ARMv7s image too.
    subtypes = (0, 9, 11) if arch == 'armv7s' else (subtype,)
    if actual_cpu != cpu or actual_subtype & 0xffffff not in subtypes:
        raise ValueError(f'{name}: wrong CPU {actual_cpu}/{actual_subtype}')
    position, deployment = 28, []
    for _ in range(commands):
        command, length = struct.unpack_from('<II', payload, position)
        if length < 8 or position + length > len(payload):
            raise ValueError(f'{name}: malformed Mach-O load command')
        if command == 0x25:
            deployment.append((7 if arch == 'i386' else 2, struct.unpack_from('<I', payload, position + 8)[0]))
        elif command == 0x32:
            deployment.append(struct.unpack_from('<II', payload, position + 8))
        position += length
    if not deployment:
        raise ValueError(f'{name}: missing deployment command')
    for platform, minimum in deployment:
        if platform != (7 if arch == 'i386' else 2) or minimum > (10 << 16):
            raise ValueError(f'{name}: platform {platform}, minimum {minimum:#x}; expected iOS10 {arch}')
    return True

def check(path, arch):
    count = sum(check_object(payload, name, arch) for name, payload in objects(path))
    if not count:
        raise ValueError('No Apple32 native objects found')
    return count

if __name__ == '__main__':
    try:
        print(f'Validated {check(*sys.argv[1:])} Apple32 native objects')
    except (ValueError, OSError, struct.error) as error:
        raise SystemExit(str(error))
