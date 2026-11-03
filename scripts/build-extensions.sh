#!/usr/bin/env bash
# Build native extensions of the portable ABI (ADR-15, docs/dev/extensions.md) and install them in the portable sysroot.
#
#   build-extensions.sh [--llvmnet <path>] [--sysroot <dir>] [--build-dir <dir>] [--source <name>=<dir>]... [--force]
#                       [<name> | <extension.json>]...
#
# The script runs from the repository (scripts/) and from an installed toolchain (share/llvmnet/extensions/ of the
# toolchain package or of the dotnet tool, with ext-gen.py beside it); it needs python3 and the .NET 10 SDK. It asks
# llvmnet (--llvmnet, else the toolchain's bin/llvmnet, else llvmnet on PATH) for its version, its LlvmNet.Runtime.dll
# and its portable sysroot. For each extension it generates the stubs, headers and package manifest with ext-gen.py,
# builds LlvmNet.Ext.<Name>.dll against that runtime, and checks every stub before installing: llvmnet compiles C files
# that include the extension's headers and declare each entry point again from the registry's or header's text,
# ext-gen.py compares the declarations with the stubs (narrow integers and their signedness included), and a link of
# those files against the assembly makes the compiler check each stub's CIL signature. llvmnet --install-extension then
# checks the package's names against the sysroot and the other installed extensions and puts it in <sysroot>/ext/<name>,
# replacing an older one in one step. A package is rebuilt only when an input changes (the description and its
# directory, the sources, the generator, this script, the runtime, llvmnet's version); --force rebuilds anyway.
#
# A description names sources as "<name>:<path>"; --source <name>=<dir> gives each one's directory, and in the
# repository the pinned ones come from scripts/fetch-sources.sh. With no extension named, the repository builds every
# extensions/<name>/extension.json. Build trees: <build-dir>/<name> (default artifacts/ext-build in the repository,
# $XDG_CACHE_HOME/llvmnet/ext-build otherwise).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
generator="$here/ext-gen.py"
repository=""
if [[ -f "$here/../llvmnet.slnx" ]]; then repository="$(cd "$here/.." && pwd)"; fi
llvmnet=""
sysroot=""
build_root=""
force=0
requested=()
declare -A given=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --llvmnet) llvmnet="$2"; shift 2 ;;
        --sysroot) sysroot="$(realpath -m "$2")"; shift 2 ;;
        --build-dir) build_root="$(realpath -m "$2")"; shift 2 ;;
        --source)
            [[ "$2" == *=* ]] || { printf 'build-extensions.sh: --source takes <name>=<dir>, not %s\n' "$2" >&2; exit 2; }
            given["${2%%=*}"]="$(realpath -m "${2#*=}")"; shift 2 ;;
        --force) force=1; shift ;;
        -h|--help) sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) printf 'build-extensions.sh: unknown option %s (--help lists them)\n' "$1" >&2; exit 2 ;;
        *) requested+=("$1"); shift ;;
    esac
done
if [[ -n "$repository" ]]; then
    bash "$repository/scripts/ensure-built.sh"
    export LLVMNET_SKIP_BUILD=1
    llvmnet="${llvmnet:-$repository/bin/llvmnet}"
elif [[ -z "$llvmnet" && -x "$here/../../../bin/llvmnet" ]]; then
    llvmnet="$(cd "$here/../../../bin" && pwd)/llvmnet"
elif [[ -z "$llvmnet" ]]; then
    llvmnet="$(command -v llvmnet || true)"
    [[ -n "$llvmnet" ]] || { printf 'build-extensions.sh: llvmnet is not on PATH; pass --llvmnet <path>\n' >&2; exit 2; }
fi
declare -A kit=()
while read -r key value; do kit["$key"]="$value"; done < <("$llvmnet" --print-extension-kit)
[[ -n "${kit[runtime]:-}" && -f "${kit[runtime]}" ]] ||
    { printf 'build-extensions.sh: %s did not name its LlvmNet.Runtime.dll (it predates extension builds?)\n' "$llvmnet" >&2; exit 2; }
runtime_dll="${kit[runtime]}"
sysroot="${sysroot:-${kit[sysroot]}}"
if [[ -n "$repository" ]]; then
    # The repository stamps the release it is working towards, so that a package stays up to date across commits.
    version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repository/Directory.Build.props")"
    build_root="${build_root:-$repository/artifacts/ext-build}"
else
    version="${kit[version]}"
    build_root="${build_root:-${XDG_CACHE_HOME:-$HOME/.cache}/llvmnet/ext-build}"
fi
descriptions=()
if [[ ${#requested[@]} -eq 0 && -n "$repository" ]]; then
    for description in "$repository"/extensions/*/extension.json; do [[ -f "$description" ]] && descriptions+=("$description"); done
elif [[ ${#requested[@]} -eq 0 ]]; then
    printf 'build-extensions.sh: name the extension.json files to build (--help)\n' >&2
    exit 2
else
    for item in "${requested[@]}"; do
        if [[ -f "$item" ]]; then descriptions+=("$(realpath "$item")")
        elif [[ -d "$item" && -f "$item/extension.json" ]]; then descriptions+=("$(realpath "$item/extension.json")")
        elif [[ -n "$repository" && -f "$repository/extensions/$item/extension.json" ]]; then descriptions+=("$repository/extensions/$item/extension.json")
        else printf 'build-extensions.sh: no extension %s (an extension.json, its directory, or a name under extensions/)\n' "$item" >&2; exit 2
        fi
    done
fi

build_extension() {
    local description="$1" directory name build generated check inputs package source names sources=() stamps=()
    directory="$(dirname "$description")"
    name="$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["name"])' "$description")"
    build="$build_root/$name"
    generated="$build/generated"
    check="$build/check"
    package="$sysroot/ext/$name"
    read -r -a names <<<"$(python3 -I "$generator" sources "$description")"
    for source in "${names[@]}"; do
        local location
        if [[ -n "${given[$source]:-}" ]]; then
            location="${given[$source]}"
            [[ -d "$location" ]] || { printf 'build-extensions.sh: --source %s=%s is not a directory\n' "$source" "$location" >&2; return 2; }
            stamps+=("$(cd "$location" && find . -type f | LC_ALL=C sort | xargs -r -d '\n' sha256sum | sha256sum | cut -d' ' -f1) $source")
        elif [[ -n "$repository" ]]; then
            location="$("$repository/scripts/fetch-sources.sh" --path "$source")"
            stamps+=("$(awk -F '\t' -v name="$source" '$1 == name' "$repository/scripts/sources.lock")")
        else
            printf 'build-extensions.sh: %s names the source %s; pass --source %s=<dir>\n' "$description" "$source" "$source" >&2
            return 2
        fi
        sources+=(--source "$source=$location")
    done
    inputs="$(
        {
            (cd "$directory" && find . -type f | LC_ALL=C sort | xargs -r -d '\n' sha256sum)
            sha256sum "$generator" "${BASH_SOURCE[0]}" "$runtime_dll" | cut -d' ' -f1
            if [[ ${#stamps[@]} -ne 0 ]]; then printf '%s\n' "${stamps[@]}"; fi
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
    local stage="$build/package"
    rm -rf "$stage"
    mkdir -p "$stage/lib"
    cp -a "$generated/package/." "$stage/"
    cp "$build/bin/$assembly.dll" "$stage/lib/"
    if [[ ${#wrappers[@]} -ne 0 ]]; then cp "$check/wrappers.o" "$stage/lib/wrappers.o"; fi
    printf '%s\n' "$inputs" >"$stage/.llvmnet-inputs"
    "$llvmnet" --portable-sysroot "$sysroot" --install-extension "$stage" >"$build/install.log" || return 1
    printf 'Built extension %s into %s: %s; %s\n' "$name" "$package" "$(head -n 1 "$build/generate.log")" "$(<"$build/verify.log")" >&2
}

mkdir -p "$sysroot/ext"
for description in "${descriptions[@]}"; do build_extension "$description"; done
