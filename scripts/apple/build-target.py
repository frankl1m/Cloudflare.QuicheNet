#!/usr/bin/env python3
"""Serialize deployment cache invalidation and Cargo's native build together."""
import fcntl
import os
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
target, arch, platform, minimum, *command = sys.argv[1:]
lock = root / 'quiche-bindgen/target/apple/native-build.lock'
lock.parent.mkdir(parents=True, exist_ok=True)
with lock.open('a') as stream:
    # Cargo clean can remove shared host fingerprints without waiting for a
    # concurrent build. Hold this lock across both checking/cleaning and Cargo.
    fcntl.flock(stream, fcntl.LOCK_EX)
    subprocess.run([str(root / 'scripts/apple/ensure-deployment.sh'), target, arch, platform, minimum], check=True, pass_fds=(stream.fileno(),))
    env = os.environ.copy()
    env['RUSTFLAGS'] = subprocess.check_output([str(root / 'scripts/apple/deployment-flags.sh'), target, minimum], text=True)
    # Keep the descriptor in Cargo as well, so an interrupted parent cannot
    # release the lock while Cargo or its compiler children are still running.
    result = subprocess.call(command, cwd=root / 'quiche-bindgen', env=env, pass_fds=(stream.fileno(),))
    if result:
        raise SystemExit(result)
    archive = root / 'quiche-bindgen/target' / target / 'release/libquiche_bindgen.a'
    subprocess.run([sys.executable, str(root / 'scripts/apple/deployment.py'), str(archive), arch, platform, minimum], check=True)
