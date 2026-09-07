#!/usr/bin/env bash
set -euo pipefail
test -n "${SPEC_PROJECT_ROOT:-}"
test -n "${SPEC_CONFIG_DIR:-}"
test -f "$SPEC_CONFIG_DIR/llvmnet.cfg"
test -f "$SPEC_CONFIG_DIR/llvmnet.xml"
test "${SPEC_MATRIX_SNAPSHOT:-}" = "$SPEC_CONFIG_DIR/matrix.sh"
test -f "$SPEC_MATRIX_SNAPSHOT"
test "$SPEC_CONFIG_DIR" != "$SPEC_PROJECT_ROOT/config/spec"
test "$(realpath "${BASH_SOURCE[0]}")" != "$SPEC_PROJECT_ROOT/tests/spec-matrix-snapshot-runner.sh"
bash "$SPEC_PROJECT_ROOT/tests/spec-matrix-runner.sh" "$@"