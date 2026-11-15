#!/usr/bin/env bash
# Makes the portable-toolchain kit (XP-04): the sources of the cross-platform fixtures (tests/xp/fixtures.tsv), each with
# the output and exit status of its native build on this Linux host, and run.sh, which compiles every fixture on any
# host with a portable llvmnet toolchain, runs it and compares. Where tests/xp checks that images built on Linux run
# everywhere, this checks that the compiler itself, its llvmnet-built LLVM and tools, run everywhere.
#
# Usage: tests/portable-toolchain/make-kit.sh [kit-dir]   (default artifacts/tests/portable-toolchain/kit, and <kit>.tar.gz)
# Needs clang-22, clang++-22 and flang-22 for the native results.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
kit="$(realpath -m "${1:-$root/artifacts/tests/portable-toolchain/kit}")"
rm -rf "$kit"
mkdir -p "$kit/src" "$kit/expected" "$kit/native"
work="$kit/native/work"
ulimit -S -s 8192
printf '# name\tsources\tflags\targuments\tenvironment\n' >"$kit/manifest.tsv"
while IFS=$'\t' read -r name _ sources flags arguments environment; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    # shellcheck disable=SC2206
    source_files=($sources)
    source_paths=()
    for source_file in "${source_files[@]}"; do
        mkdir -p "$kit/src/$(dirname "$source_file")"
        cp "$root/tests/$source_file" "$kit/src/$source_file"
        # Local headers and included sources beside it.
        { grep -ho '#include "[^"]*"' "$root/tests/$source_file" || true; } | sed 's/#include "\(.*\)"/\1/' | while read -r included; do
            cp "$root/tests/$(dirname "$source_file")/$included" "$kit/src/$(dirname "$source_file")/"
        done
        source_paths+=("$root/tests/$source_file")
    done
    # shellcheck disable=SC2206
    flag_list=($flags)
    native_flags=("${flag_list[@]/#--isa=x86-64-v/-march=x86-64-v}")
    case "${source_files[0]##*.}" in
        c) compiler=(clang-22 "${native_flags[@]}" "${source_paths[@]}" -lm -latomic) ;;
        cpp) compiler=(clang++-22 "${native_flags[@]}" "${source_paths[@]}") ;;
        f90) compiler=(flang-22 "${native_flags[@]}" "${source_paths[@]}") ;;
        *) printf 'Unknown source kind for %s\n' "$name" >&2; exit 2 ;;
    esac
    "${compiler[@]}" -o "$kit/native/$name"
    run_arguments=()
    # shellcheck disable=SC2206
    if [[ "$arguments" != - ]]; then run_arguments=($arguments); fi
    run_environment=()
    if [[ "$environment" != - ]]; then IFS=';' read -r -a run_environment <<<"$environment"; fi
    rm -rf "$work"
    mkdir -p "$work"
    status=0
    (cd "$work" && env "${run_environment[@]}" "$kit/native/$name" "${run_arguments[@]}" >"$kit/expected/$name.txt" 2>/dev/null) || status=$?
    printf '%s\n' "$status" >"$kit/expected/$name.status"
    printf '%s\t%s\t%s\t%s\t%s\n' "$name" "$sources" "$flags" "$arguments" "$environment" >>"$kit/manifest.tsv"
done <"$root/tests/xp/fixtures.tsv"
rm -rf "$kit/native"
cp "$root/tests/portable-toolchain/run.sh" "$kit/run.sh"
printf 'llvmnet %s portable-toolchain kit, %s\n' "$(git -C "$root" describe --always --dirty 2>/dev/null || echo unknown)" "$(date -u +%Y-%m-%d)" >"$kit/VERSION"
tar -C "$(dirname "$kit")" -czf "$kit.tar.gz" "$(basename "$kit")"
printf 'Kit with %d fixtures in %s (and %s)\n' "$(grep -vc '^#' "$kit/manifest.tsv")" "$kit" "$kit.tar.gz"
