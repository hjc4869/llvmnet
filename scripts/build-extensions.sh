#!/usr/bin/env bash
# Build native extensions of the portable ABI (ADR-15, docs/dev/extensions.md) and install them in the portable sysroot.
#
#   scripts/build-extensions.sh [--sysroot <dir>] [--build-dir <dir>] [--force] [<name> | <extension.json>]...
#
# With no extension named, every extensions/<name>/extension.json is built. For each one the script fetches the pinned
# sources its description names (scripts/fetch-sources.sh), generates the stubs, headers and package manifest with
# scripts/ext-gen.py, builds LlvmNet.Ext.<Name>.dll against LlvmNet.Runtime, and checks every stub before installing:
# llvmnet compiles C files that include the extension's headers and declare each entry point again from the registry's
# text, ext-gen.py compares the declarations with the stubs (narrow integers and their signedness included), and a link
# of those files against the assembly makes the compiler check each stub's CIL signature. The package goes to
# <sysroot>/ext/<name> (default sysroot/dotnet64-v2), replacing an older one in one step, and is rebuilt only when an
# input changes (the description and its directory, the generator, this script, the pinned sources, LlvmNet.Runtime,
# llvmnet's version); --force rebuilds anyway. Build trees: <build-dir>/<name> (default artifacts/ext-build).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sysroot="$root/sysroot/dotnet64-v2"
build_root="$root/artifacts/ext-build"
force=0
requested=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --sysroot) sysroot="$(realpath -m "$2")"; shift 2 ;;
        --build-dir) build_root="$(realpath -m "$2")"; shift 2 ;;
        --force) force=1; shift ;;
        -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) requested+=("$1"); shift ;;
    esac
done
bash "$root/scripts/ensure-built.sh"
export LLVMNET_SKIP_BUILD=1
llvmnet="$root/bin/llvmnet"
runtime_dll="$root/src/LlvmNet.Runtime/bin/Release/net10.0/LlvmNet.Runtime.dll"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
generator="$root/scripts/ext-gen.py"
descriptions=()
if [[ ${#requested[@]} -eq 0 ]]; then
    for description in "$root"/extensions/*/extension.json; do [[ -f "$description" ]] && descriptions+=("$description"); done
else
    for item in "${requested[@]}"; do
        if [[ -f "$item" ]]; then descriptions+=("$(realpath "$item")")
        elif [[ -f "$root/extensions/$item/extension.json" ]]; then descriptions+=("$root/extensions/$item/extension.json")
        else printf 'build-extensions.sh: no extension %s (extensions/%s/extension.json)\n' "$item" "$item" >&2; exit 2
        fi
    done
fi

build_extension() {
    local description="$1" directory name build generated check inputs package stage source sources=() names
    directory="$(dirname "$description")"
    name="$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["name"])' "$description")"
    build="$build_root/$name"
    generated="$build/generated"
    check="$build/check"
    package="$sysroot/ext/$name"
    read -r -a names <<<"$(python3 -I "$generator" sources "$description")"
    for source in "${names[@]}"; do sources+=(--source "$source=$("$root/scripts/fetch-sources.sh" --path "$source")"); done
    inputs="$(
        {
            (cd "$directory" && find . -type f | LC_ALL=C sort | xargs sha256sum)
            sha256sum "$generator" "${BASH_SOURCE[0]}" "$runtime_dll"
            for source in "${names[@]}"; do awk -F '\t' -v name="$source" '$1 == name' "$root/scripts/sources.lock"; done
            printf 'llvmnet %s\n' "$version"
        } | sha256sum | cut -d' ' -f1
    )"
    if [[ "$force" -eq 0 && -f "$package/.llvmnet-inputs" && "$(<"$package/.llvmnet-inputs")" == "$inputs" ]]; then
        printf 'Extension %s is up to date in %s\n' "$name" "$package" >&2
        return 0
    fi
    rm -rf "$build"
    mkdir -p "$build"
    python3 -I "$generator" generate "$description" --output "$generated" --llvmnet-version "$version" --compiler "$llvmnet" "${sources[@]}" \
        >"$build/generate.log" ||
        { cat "$build/generate.log" >&2; return 1; }
    local assembly
    assembly="$(basename "$generated"/project/*.csproj .csproj)"
    dotnet build "$generated/project/$assembly.csproj" -c Release --nologo -v quiet -clp:ErrorsOnly -p:LlvmNetRuntime="$runtime_dll" \
        --artifacts-path "$build/msbuild" -o "$build/bin" >"$build/dotnet.log" 2>&1 || { cat "$build/dotnet.log" >&2; return 1; }
    # The check files compile against the package's headers alone, as a program would see them once it is installed.
    mkdir -p "$check"
    local file ir=() objects=()
    # The wrappers of the entry points that pass structures by value are part of the package, compiled for the portable
    # target, and checked with the rest (NX-07).
    local wrappers=()
    [[ -f "$generated/wrappers.c" ]] && wrappers=("$generated/wrappers.c")
    for file in "$generated"/check/*.c "${wrappers[@]}"; do
        local stem
        stem="$(basename "$file" .c)"
        if [[ "$stem" != main ]]; then
            "$llvmnet" -O1 -I "$generated/package/include" -S -emit-llvm "$file" -o "$check/$stem.ll"
            ir+=("$check/$stem.ll")
        fi
        "$llvmnet" -O1 -I "$generated/package/include" -c "$file" -o "$check/$stem.o"
        objects+=("$check/$stem.o")
    done
    python3 -I "$generator" verify "$generated" "${ir[@]}" >"$build/verify.log" || { cat "$build/verify.log" >&2; return 1; }
    "$llvmnet" "${objects[@]}" --cil-reference "$build/bin/$assembly.dll" -o "$check/image/check.dll" >"$build/link.log" 2>&1 ||
        { cat "$build/link.log" >&2; return 1; }
    dotnet "$check/image/check.dll"
    stage="$package.new"
    rm -rf "${stage:?}"
    mkdir -p "$stage/lib"
    cp -a "$generated/package/." "$stage/"
    cp "$build/bin/$assembly.dll" "$stage/lib/"
    if [[ ${#wrappers[@]} -ne 0 ]]; then cp "$check/wrappers.o" "$stage/lib/wrappers.o"; fi
    printf '%s\n' "$inputs" >"$stage/.llvmnet-inputs"
    rm -rf "${package:?}.old"
    [[ -d "$package" ]] && mv "$package" "$package.old"
    mv "$stage" "$package"
    rm -rf "${package:?}.old"
    printf 'Built extension %s into %s: %s; %s\n' "$name" "$package" "$(head -n 1 "$build/generate.log")" "$(<"$build/verify.log")" >&2
}

mkdir -p "$sysroot/ext"
for description in "${descriptions[@]}"; do build_extension "$description"; done
