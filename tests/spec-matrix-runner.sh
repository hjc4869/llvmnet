#!/usr/bin/env bash
set -euo pipefail
test "$SPEC_OPT_LEVEL" = 3
test "$SPEC_VECTORIZE" = 0
test "$SPEC_REBUILD" = 1
test "$SPEC_REUSE_BUILD" = 0
test "$5" = test
case "$4" in *_s) test "$SPEC_THREADS" = 4 ;; *) test "$SPEC_THREADS" = 1 ;; esac
case "$4:$3" in
    100.pass_r:system) printf 'Success: 1x100.pass_r\n' ;;
    100.pass_r:portable) printf 'Success: 100.pass_r\n' ;;
    201.fail_s:system) printf 'Build completed without output validation.\n' ;;
    201.fail_s:portable) exit 124 ;;
    *) exit 2 ;;
esac