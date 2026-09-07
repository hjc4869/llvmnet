#!/usr/bin/env bash
set -euo pipefail
test "$SPEC_OPT_LEVEL" = 3
test "$SPEC_VECTORIZE" = 0
test "$SPEC_REBUILD" = 1
test "$5" = test
case "$4:$3" in
    100.pass_r:system) printf 'Success: 1x100.pass_r\n' ;;
    100.pass_r:portable) printf 'Success: 100.pass_r\n' ;;
    201.fail_s:system) printf 'Build completed without output validation.\n' ;;
    201.fail_s:portable) exit 124 ;;
    *) exit 2 ;;
esac