#!/usr/bin/env python3
"""Read deployment commands from every native object of a thin Apple archive."""
from pathlib import Path
import struct
import sys

PLATFORMS = {1: 'MACOS', 2: 'IOS', 3: 'TVOS', 6: 'MACCATALYST', 7: 'IOSSIMULATOR', 8: 'TVOSSIMULATOR'}

def version_number(value):
    parts = [int(part) for part in value.split('.')]
    parts += [0] * (3 - len(parts))
    return (parts[0] << 16) | (parts[1] << 8) | parts[2]

def inspect_archive(path, arch):
    data, offset = Path(path).read_bytes(), 8
    if data[:8] != b'!<arch>\n':
        raise ValueError(f'Not a thin archive: {path}')
    while offset < len(data):
        header = data[offset:offset + 60]
        if len(header) != 60 or header[58:] != b'`\n':
            raise ValueError(f'Invalid archive header: {path}/{offset}')
        size = int(header[48:58])
        payload = data[offset + 60:offset + 60 + size]
        name = header[:16].decode('ascii').strip()
        if name.startswith('#1/'):
            length = int(name[3:])
            name = payload[:length].rstrip(b'\0').decode('utf-8')
            payload = payload[length:]
        commands_found = []
        if payload[:4] == b'\xcf\xfa\xed\xfe':
            count = struct.unpack_from('<I', payload, 16)[0]
            position = 32
            for _ in range(count):
                command, length = struct.unpack_from('<II', payload, position)
                if command == 0x32:
                    platform, minimum = struct.unpack_from('<II', payload, position + 8)
                    commands_found.append((PLATFORMS.get(platform, str(platform)), minimum))
                elif command in (0x24, 0x25, 0x2f):
                    minimum = struct.unpack_from('<I', payload, position + 8)[0]
                    platform = {0x24: 'MACOS', 0x25: 'IOSSIMULATOR' if arch == 'x86_64' else 'IOS',
                                0x2f: 'TVOSSIMULATOR' if arch == 'x86_64' else 'TVOS'}[command]
                    commands_found.append((platform, minimum))
                position += length
        yield name, commands_found
        offset += 60 + size + size % 2
    if offset != len(data):
        raise ValueError(f'Truncated archive: {path}')

def check(path, arch, platform, minimum):
    count = 0
    for name, commands in inspect_archive(path, arch):
        for actual_platform, actual_minimum in commands:
            count += 1
            if actual_platform != platform or actual_minimum > version_number(minimum):
                version = f'{actual_minimum >> 16}.{(actual_minimum >> 8) & 255}.{actual_minimum & 255}'
                raise ValueError(f'{name}: {actual_platform} {version}; expected {platform} <= {minimum}')
    if not count:
        raise ValueError(f'No deployment commands: {path}')
    return count

if __name__ == '__main__':
    try:
        count = check(*sys.argv[1:])
        print(f'Validated {count} native object deployment commands')
    except (ValueError, OSError) as error:
        raise SystemExit(str(error))
