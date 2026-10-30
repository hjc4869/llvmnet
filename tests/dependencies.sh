#!/usr/bin/env bash
# tags: integration ffmpeg
# serial: 10
# timeout: 3600
# zlib, libxml2 and dav1d built with llvmnet for both modes, each exercised by a small program whose output must
# match the same program built natively from the system-mode IR.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/dependencies"
mkdir -p "$output"
libraries=("${@:-zlib libxml2 dav1d}")
for runtime in system portable; do
    prefix="$root/artifacts/deps-$runtime"
    if [[ $# -eq 0 ]]; then LLVMNET_RUNTIME="$runtime" bash "$root/scripts/build-dependencies.sh" >"$output/build-$runtime.log" 2>&1; fi
    for library in ${libraries[*]}; do
        case "$library" in
            zlib) archive="$prefix/lib/libz.a" ;;
            libxml2) archive="$prefix/lib/libxml2.a" ;;
            dav1d) archive="$prefix/lib/libdav1d.a" ;;
            *) printf 'Unknown dependency test: %s\n' "$library" >&2; exit 2 ;;
        esac
        "$llvmnet" --runtime="$runtime" -I"$prefix/include" -I"$prefix/include/libxml2" \
            "$root/tests/dependencies/$library.c" "$archive" -lm --keep-ir "$output/$library-$runtime.bc" -o "$output/$library-$runtime.dll"
        if [[ "$runtime" == system ]]; then
            clang-22 -O1 "$output/$library-system.bc" -lm -o "$output/$library-native"
            "$output/$library-native" >"$output/$library-native.txt"
        fi
        dotnet "$output/$library-$runtime.dll" >"$output/$library-$runtime.txt"
        diff -u "$output/$library-native.txt" "$output/$library-$runtime.txt"
        printf 'PASS: native and %s CIL %s integration\n' "$runtime" "$library"
    done
done
