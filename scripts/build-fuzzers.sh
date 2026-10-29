#!/usr/bin/env bash
# Build the pinned random program generators for differential fuzzing (plan item FD-16).
#   scripts/build-fuzzers.sh [csmith|yarpgen]...     default: both
# Installs artifacts/tools/<name>/bin/<name> and prints one prefix per tool; csmith also gets include/
# (csmith.h and its safe-math runtime headers). Rebuilds only when the pinned source changes.
# Needs cmake, m4 (csmith) and clang++-22.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tools=("$@")
[[ ${#tools[@]} -gt 0 ]] || tools=(csmith yarpgen)
for tool in "${tools[@]}"; do
    prefix="$root/artifacts/tools/$tool"
    upstream="$("$root/scripts/fetch-sources.sh" --path "$tool")"
    stamp="$prefix/.llvmnet-source"
    if [[ -x "$prefix/bin/$tool" && -f "$stamp" && "$(<"$stamp")" == "$(<"$upstream/.llvmnet-source")" ]]; then
        printf '%s\n' "$prefix"
        continue
    fi
    build="$prefix/build"
    rm -rf "${prefix:?}"
    mkdir -p "$build" "$prefix/bin"
    {
        # The unpacked sources sit inside this repository; keep yarpgen from recording llvmnet's git hash.
        cmake -S "$upstream" -B "$build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
            -DCMAKE_C_COMPILER=clang-22 -DCMAKE_CXX_COMPILER=clang++-22 -DCMAKE_DISABLE_FIND_PACKAGE_Git=TRUE
        cmake --build "$build" -j "${JOBS:-$(nproc)}"
        case "$tool" in
            csmith) cmake --install "$build" ;;
            yarpgen) cp "$build/yarpgen" "$prefix/bin/yarpgen" ;;
        esac
    } >"$prefix/build.log" 2>&1 || { tail -40 "$prefix/build.log" >&2; exit 1; }
    cp "$upstream/.llvmnet-source" "$stamp"
    printf '%s\n' "$prefix"
done
