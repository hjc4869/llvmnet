#!/usr/bin/env bash
set -euo pipefail
test "$SPEC_REBUILD" = 1
test "$5" = test
case "$4" in *_s) test "$SPEC_THREADS" = 4 ;; *) test "$SPEC_THREADS" = 1 ;; esac
if [[ "$3" == system && "$4" == "${SPEC_ORDER_FAIL:-}" || "$3" == portable && "$4" == "${SPEC_ORDER_PORTABLE_FAIL:-}" ]]; then
    printf 'Compilation succeeded without output validation.\n'
else
    printf 'Success: 1x%s\n' "$4"
fi