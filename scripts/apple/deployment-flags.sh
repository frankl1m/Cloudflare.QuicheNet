#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
flags="${RUSTFLAGS:-}"
# Legacy archives already validated at the requested minimum retain their
# Cargo fingerprint. New or incompatible builds record the minimum in it.
if [[ -f "$root/quiche-bindgen/target/apple/deployment-flags/$1" ]]; then
    flags+=" --cfg=quiche_deployment_target=\"$2\""
fi
printf '%s' "$flags"
