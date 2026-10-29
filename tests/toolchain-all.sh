#!/usr/bin/env bash
# Compatibility wrapper: the full dual-ABI toolchain gate, including packaging and isolated deployment.
set -euo pipefail
exec bash "$(cd "$(dirname "$0")" && pwd)/run.sh" --tag core --tag toolchain "$@"
