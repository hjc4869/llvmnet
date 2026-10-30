#!/usr/bin/env bash
# The full toolchain gate in both ABIs, including packaging and isolated deployment; arguments go to tests/run.sh.
set -euo pipefail
exec bash "$(cd "$(dirname "$0")" && pwd)/run.sh" --tag core --tag toolchain "$@"
