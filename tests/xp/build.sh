#!/usr/bin/env bash
# Builds the cross-platform bundle (XP-01 to XP-03): for every fixture in tests/xp/fixtures.tsv, the native build's
# output and exit status on this Linux host, the portable image, and for fixtures marked "aot" a relocatable NativeAOT
# project. tests/xp/run.sh runs the bundle on any .NET host; the images must reproduce the native results there.
#
# Usage: tests/xp/build.sh [bundle-dir]      (default artifacts/tests/xp/bundle; also writes <bundle-dir>.tar.gz)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bundle="${1:-$root/artifacts/tests/xp/bundle}"
bundle="$(realpath -m "$bundle")"
bash "$root/scripts/ensure-built.sh"
export LLVMNET_SKIP_BUILD=1
llvmnet="$root/bin/llvmnet"
rm -rf "$bundle"
mkdir -p "$bundle/images" "$bundle/expected" "$bundle/aot" "$bundle/native"
work="$bundle/native/work"
{
    printf '# name\taot\targuments\tenvironment\n'
} >"$bundle/manifest.tsv"
count=0
while IFS=$'\t' read -r name aot sources flags arguments environment; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    count=$((count + 1))
    # shellcheck disable=SC2206
    source_files=($sources)
    source_paths=()
    for source_file in "${source_files[@]}"; do source_paths+=("$root/tests/$source_file"); done
    # shellcheck disable=SC2206
    flag_list=($flags)
    case "${source_files[0]##*.}" in
        c) compiler=(clang-22 "${flag_list[@]}" "${source_paths[@]}" -lm -latomic -o "$bundle/native/$name") ;;
        cpp) compiler=(clang++-22 "${flag_list[@]}" "${source_paths[@]}" -o "$bundle/native/$name") ;;
        f90) compiler=(flang-22 "${flag_list[@]}" "${source_paths[@]}" -o "$bundle/native/$name") ;;
        *) printf 'Unknown source kind for %s\n' "$name" >&2; exit 2 ;;
    esac
    if [[ "$flags" == *-fopenmp* ]]; then compiler+=(-fopenmp); fi
    "${compiler[@]}"
    run_arguments=()
    if [[ "$arguments" != - ]]; then
        # shellcheck disable=SC2206
        run_arguments=($arguments)
    fi
    run_environment=()
    if [[ "$environment" != - ]]; then IFS=';' read -r -a run_environment <<<"$environment"; fi
    rm -rf "$work"
    mkdir -p "$work"
    status=0
    (cd "$work" && env "${run_environment[@]}" "$bundle/native/$name" "${run_arguments[@]}" >"$bundle/expected/$name.txt" 2>/dev/null) || status=$?
    printf '%s\n' "$status" >"$bundle/expected/$name.status"
    "$llvmnet" --runtime=portable "${flag_list[@]}" "${source_paths[@]}" -o "$bundle/images/$name.dll"
    if [[ "$aot" == aot ]]; then
        "$llvmnet" --runtime=portable "${flag_list[@]}" "${source_paths[@]}" --aot-project "$bundle/aot/$name"
    fi
    printf '%s\t%s\t%s\t%s\n' "$name" "$aot" "$arguments" "$environment" >>"$bundle/manifest.tsv"
done <"$root/tests/xp/fixtures.tsv"
rm -rf "$work"
cp "$root/tests/xp/run.sh" "$bundle/run.sh"
chmod +x "$bundle/run.sh"
# The NativeAOT projects publish with the .NET 10 SDK wherever the bundle runs, never with a preview of a later one.
cp "$root/global.json" "$bundle/global.json"
{
    printf 'llvmnet %s\n' "$(git -C "$root" describe --always --dirty 2>/dev/null || echo unknown)"
    printf 'built %s on %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(uname -sm)"
    printf 'dotnet %s\n' "$(dotnet --version)"
} >"$bundle/VERSION"
tar czf "$bundle.tar.gz" -C "$(dirname "$bundle")" "$(basename "$bundle")"
printf 'Bundle: %d fixtures in %s (%s)\n' "$count" "$bundle" "$(du -sh "$bundle.tar.gz" | cut -f1)"
