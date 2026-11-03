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
# The native results are recorded under Linux's usual 8 MB stack limit, which is what the portable runtime reports for
# RLIMIT_STACK on hosts without one (Windows, macOS); GitHub's Linux runners default to 16 MB. run.sh sets the same.
ulimit -S -s 8192
{
    printf '# name\taot\targuments\tenvironment\trequires\n'
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
# Native extension fixtures (NX-05): portable images linked with an extension from this toolchain, whose expected output
# is fixed (every conforming host library prints it), run only where the host library can be found (run.sh).
bash "$root/scripts/build-extensions.sh" gles vulkan >/dev/null 2>"$bundle/native/extensions.log" || { cat "$bundle/native/extensions.log" >&2; exit 1; }
extension_fixtures=("gles:-lEGL -lGLESv2" "vulkan:-lvulkan")
for entry in "${extension_fixtures[@]}"; do
    fixture="${entry%%:*}"
    read -r -a link <<<"${entry#*:}"
    count=$((count + 1))
    "$llvmnet" --runtime=portable -O1 "$root/tests/extensions/$fixture.c" "${link[@]}" -o "$bundle/images/$fixture.dll"
    "$llvmnet" --runtime=portable -O1 "$root/tests/extensions/$fixture.c" "${link[@]}" --aot-project "$bundle/aot/$fixture"
    cp "$root/tests/extensions/$fixture.expected" "$bundle/expected/$fixture.txt"
    printf '0\n' >"$bundle/expected/$fixture.status"
    printf '%s\taot\t-\t-\textension:%s\n' "$fixture" "$fixture" >>"$bundle/manifest.tsv"
done
# Structures by value (NX-07): a test extension over a native library that each host builds with its own compiler from
# native-src/structs (run.sh), except Linux ARM64, whose runner has no compiler and gets one cross-compiled here.
farm="$bundle/native/toolchain"
rm -rf "$farm"
mkdir -p "$farm/sysroot/dotnet64-v2/ext"
for entry in "$root"/*; do [[ "${entry##*/}" == sysroot ]] || ln -s "$entry" "$farm/"; done
for entry in "$root"/sysroot/*; do [[ "${entry##*/}" == dotnet64-v2 ]] || ln -s "$entry" "$farm/sysroot/"; done
for entry in "$root"/sysroot/dotnet64-v2/* "$root"/sysroot/dotnet64-v2/.llvmnet-*; do
    [[ "${entry##*/}" == ext ]] || ln -s "$entry" "$farm/sysroot/dotnet64-v2/"
done
bash "$root/scripts/build-extensions.sh" --sysroot "$farm/sysroot/dotnet64-v2" --build-dir "$bundle/native/ext-build" \
    "$root/tests/extensions/structs/extension.json" >/dev/null 2>"$bundle/native/structs.log" || { cat "$bundle/native/structs.log" >&2; exit 1; }
structs="$root/tests/extensions/structs"
mkdir -p "$bundle/native-src/structs" "$bundle/native-lib/linux-x86_64" "$bundle/native-lib/linux-aarch64"
cp -r "$structs/native.c" "$structs/include" "$bundle/native-src/structs/"
clang-22 -shared -fPIC -O1 -I"$structs/include" "$structs/native.c" -o "$bundle/native-lib/linux-x86_64/libllvmnet-structs.so"
# Debian's cross packages (gcc-aarch64-linux-gnu) give clang the libraries and linker; without them the ARM64 runner
# skips the fixture.
if ! clang-22 --target=aarch64-linux-gnu -shared -fPIC -O1 -I"$structs/include" "$structs/native.c" \
    -o "$bundle/native-lib/linux-aarch64/libllvmnet-structs.so" 2>"$bundle/native/structs-aarch64.log"; then
    rmdir "$bundle/native-lib/linux-aarch64" 2>/dev/null || true
fi
clang-22 -O1 -I"$structs/include" "$root/tests/extensions/structs.c" -L"$bundle/native-lib/linux-x86_64" -l:libllvmnet-structs.so \
    -o "$bundle/native/structs"
LD_LIBRARY_PATH="$bundle/native-lib/linux-x86_64" "$bundle/native/structs" >"$bundle/expected/structs.txt"
printf '0\n' >"$bundle/expected/structs.status"
LLVMNET_ROOT="$farm" "$llvmnet" --runtime=portable -O1 "$root/tests/extensions/structs.c" -lllvmnet-structs -o "$bundle/images/structs.dll"
LLVMNET_ROOT="$farm" "$llvmnet" --runtime=portable -O1 "$root/tests/extensions/structs.c" -lllvmnet-structs --aot-project "$bundle/aot/structs"
printf 'structs\taot\t-\t-\tnative:structs\n' >>"$bundle/manifest.tsv"
count=$((count + 1))
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
