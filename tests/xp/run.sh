#!/usr/bin/env bash
# Runs a cross-platform bundle made by tests/xp/build.sh on this host and compares every portable image with the
# native result recorded in the bundle (XP-01 to XP-03). Works on Linux, macOS and Windows (Git Bash or MSYS2).
#
# Usage: run.sh [options] <bundle-dir>
#   --aot             also publish the NativeAOT projects with "dotnet publish" and run the executables
#   --aot-only        publish and run the NativeAOT projects, skipping the JIT runs
#   --rid <rid>       NativeAOT runtime identifier (default: the host's, from "dotnet --info")
#   --dotnet <path>   the dotnet executable (default: dotnet on PATH, or $LLVMNET_DOTNET)
#   --only <names>    comma-separated fixture names to run
#   --jobs <n>        NativeAOT publishes to run at once (default 2)
#   --keep            keep the scratch directories of passing fixtures
# Needs bash, diff and the .NET 10 runtime; --aot needs the .NET 10 SDK and the host's native toolchain
# (Visual Studio C++ tools on Windows, Xcode command-line tools on macOS, clang on Linux).
# Results: <bundle>/results/<host>/results.tsv (name, mode, PASS/FAIL/SKIP, note) and one directory per fixture.
# Written for bash 3.2 (macOS): no "wait -n", and empty arrays are expanded with the ${array[@]+...} form.
set -o pipefail
aot=0 jit=1 rid="" dotnet="${LLVMNET_DOTNET:-dotnet}" only="" jobs=2 keep=0 bundle=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --aot) aot=1; shift ;;
        --aot-only) aot=1; jit=0; shift ;;
        --rid) rid="$2"; shift 2 ;;
        --dotnet) dotnet="$2"; shift 2 ;;
        --only) only="$2"; shift 2 ;;
        --jobs) jobs="$2"; shift 2 ;;
        --keep) keep=1; shift ;;
        -h|--help) sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) printf 'Unknown option %s\n' "$1" >&2; exit 2 ;;
        *) bundle="$1"; shift ;;
    esac
done
if [[ -z "$bundle" ]]; then bundle="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; fi
bundle="$(cd "$bundle" && pwd)"
if [[ ! -f "$bundle/manifest.tsv" ]]; then printf '%s is not a bundle (no manifest.tsv)\n' "$bundle" >&2; exit 2; fi
system="$(uname -s)"
case "$system" in
    MINGW*|MSYS*|CYGWIN*) system=windows; exe=.exe ;;
    Darwin) system=macos; exe="" ;;
    *) system="$(tr '[:upper:]' '[:lower:]' <<<"$system")"; exe="" ;;
esac
host="$system-$(uname -m | tr '[:upper:]' '[:lower:]')"
# Results of a cross-published runtime identifier are kept apart from the host's own.
results="$bundle/results/$host${rid:+-$rid}"
mkdir -p "$results"
timeout_command=()
if command -v timeout >/dev/null 2>&1; then timeout_command=(timeout 600); elif command -v gtimeout >/dev/null 2>&1; then timeout_command=(gtimeout 600); fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# The stack limit build.sh recorded the native results under; Windows and macOS images report 8 MB without it.
ulimit -S -s 8192 2>/dev/null || true
if [[ $aot -eq 1 && -z "$rid" ]]; then
    rid="$("$dotnet" --info 2>/dev/null | sed -n 's/^ *RID: *//p' | head -1 | tr -d '\r')"
    if [[ -z "$rid" ]]; then printf 'Cannot determine the runtime identifier; pass --rid\n' >&2; exit 2; fi
    results="$bundle/results/$host"
fi
mkdir -p "$results"
{
    printf 'host %s\n' "$(uname -a)"
    printf 'dotnet %s\n' "$("$dotnet" --version 2>/dev/null || echo unknown)"
    cat "$bundle/VERSION" 2>/dev/null
} | tee "$results/host.txt"
passed=0 failed=0 skipped=0
printf '# name\tmode\tresult\tnote\n' >"$results/results.tsv"
record() {
    local name="$1" mode="$2" result="$3" note="${4:-}"
    printf '%s\t%s\t%s\t%s\n' "$name" "$mode" "$result" "$note" >>"$results/results.tsv"
    printf '%-7s %-24s %-4s %s\n' "$result" "$name" "$mode" "$note"
    case "$result" in PASS) passed=$((passed + 1)) ;; FAIL) failed=$((failed + 1)) ;; SKIP) skipped=$((skipped + 1)) ;; esac
}
selected() { [[ -z "$only" ]] || [[ ",$only," == *",$1,"* ]]; }
# What a fixture needs from the host besides .NET (the manifest's fifth column): "extension:gles" needs EGL and GLES
# libraries, found where LLVMNET_EXT_GLES_DIR names (ANGLE on Windows and macOS) or, on Linux, on the loader's path;
# "extension:vulkan" needs a Vulkan loader or driver library, where LLVMNET_EXT_VULKAN_DIR names (SwiftShader on macOS)
# or the system's (libvulkan.so.1, vulkan-1.dll). Prints the reason and fails when the host lacks it, so the fixture is
# skipped.
unavailable() {
    local libraries=""
    if [[ "$system" == linux ]]; then libraries="$(ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null)"; fi
    case "$1" in
        extension:gles)
            if [[ -n "${LLVMNET_EXT_GLES_DIR:-}" ]] || grep -q 'libEGL\.so\.1 ' <<<"$libraries"; then return 1; fi
            printf 'no EGL and GLES libraries (set LLVMNET_EXT_GLES_DIR to a directory with ANGLE'"'"'s libEGL and libGLESv2)'
            return 0 ;;
        native:*)
            native_library "${1#native:}" >/dev/null && return 1
            printf 'no compiler for the native test library %s (clang on macOS, Visual Studio'"'"'s cl on Windows)' "${1#native:}"
            return 0 ;;
        extension:vulkan)
            if [[ -n "${LLVMNET_EXT_VULKAN_DIR:-}" ]] || grep -q 'libvulkan\.so\.1 ' <<<"$libraries" ||
                [[ "$system" == windows && -f "${WINDIR:-C:/Windows}/System32/vulkan-1.dll" ]]; then return 1; fi
            printf 'no Vulkan library (set LLVMNET_EXT_VULKAN_DIR to a directory with SwiftShader'"'"'s vk_swiftshader or MoltenVK)'
            return 0 ;;
    esac
    return 1
}
# The directory of a native test library the bundle carries as source (native-src/<name>), built here with the host's
# compiler, or cross-compiled by build.sh for Linux; prints it, or fails when the host has no compiler (NX-07).
native_library() {
    local name="$1" directory="$results/native-lib/$name" prebuilt
    prebuilt="$bundle/native-lib/$system-$(uname -m)"
    if [[ -f "$directory/.built" ]]; then printf '%s\n' "$directory"; return 0; fi
    mkdir -p "$directory"
    if [[ "$system" == linux && -d "$prebuilt" ]]; then
        cp "$prebuilt"/* "$directory/"
    elif [[ "$system" == macos ]] && command -v clang >/dev/null 2>&1; then
        clang -dynamiclib -O1 -I"$bundle/native-src/$name/include" "$bundle/native-src/$name/native.c" \
            -o "$directory/libllvmnet-$name.dylib" >"$directory/build.log" 2>&1 || return 1
    elif [[ "$system" == windows ]]; then
        local vcvars
        vcvars="$(ls "/c/Program Files/Microsoft Visual Studio"/*/*/VC/Auxiliary/Build/vcvars64.bat 2>/dev/null | tail -1)"
        [[ -n "$vcvars" ]] || return 1
        printf '@call "%s" >nul\r\n@cl /nologo /LD /O1 /I include native.c /Fe:llvmnet-%s.dll\r\n' "$(cygpath -w "$vcvars")" "$name" \
            >"$bundle/native-src/$name/build.bat"
        (cd "$bundle/native-src/$name" && cmd //c build.bat </dev/null >"$directory/build.log" 2>&1) || return 1
        cp "$bundle/native-src/$name/llvmnet-$name.dll" "$directory/"
    else
        return 1
    fi
    touch "$directory/.built"
    printf '%s\n' "$directory"
}
# The fixture's environment, with LLVMNET_EXT_<NAME>_DIR naming its native test library when it needs one.
fixture_environment() {
    local environment="$1" requires="$2" directory variable
    if [[ "$requires" == native:* ]] && directory="$(native_library "${requires#native:}")"; then
        if [[ "$system" == windows ]]; then directory="$(cygpath -w "$directory")"; fi
        variable="LLVMNET_EXT_$(tr '[:lower:]-' '[:upper:]_' <<<"${requires#native:}")_DIR=$directory"
        if [[ "$environment" == - ]]; then environment="$variable"; else environment="$environment;$variable"; fi
    fi
    printf '%s' "$environment"
}
# Runs one image or executable in a fresh directory and compares stdout and the exit status with the native record.
check() {
    local name="$1" mode="$2" arguments="$3" environment="$4"
    shift 4
    local directory="$results/$name-$mode"
    rm -rf "$directory"
    mkdir -p "$directory/work"
    local run_arguments=() run_environment=()
    # shellcheck disable=SC2206
    if [[ "$arguments" != - ]]; then run_arguments=($arguments); fi
    if [[ "$environment" != - ]]; then IFS=';' read -r -a run_environment <<<"$environment"; fi
    local status=0 expected_status
    expected_status="$(tr -d '\r' <"$bundle/expected/$name.status")"
    (cd "$directory/work" && env ${run_environment[@]+"${run_environment[@]}"} ${timeout_command[@]+"${timeout_command[@]}"} "$@" ${run_arguments[@]+"${run_arguments[@]}"} >"$directory/stdout.txt" 2>"$directory/stderr.txt") || status=$?
    printf '%s\n' "$status" >"$directory/status.txt"
    local note=""
    if [[ "$status" != "$expected_status" ]]; then note="exit status $status, native $expected_status"; fi
    if ! diff -u "$bundle/expected/$name.txt" "$directory/stdout.txt" >"$directory/stdout.diff" 2>&1; then
        note="${note:+$note; }stdout differs ($(grep -c '^[-+][^-+]' "$directory/stdout.diff") lines)"
    fi
    if [[ -n "$note" ]]; then
        record "$name" "$mode" FAIL "$note"
        head -c 2000 "$directory/stderr.txt"
        head -40 "$directory/stdout.diff"
        return 1
    fi
    record "$name" "$mode" PASS
    if [[ $keep -eq 0 ]]; then rm -rf "$directory/work"; fi
}
# Publishes one NativeAOT project into <results>/<name>-publish (the <name>-aot directory is the run's, made by check).
publish() {
    local name="$1" directory="$results/$name-publish"
    rm -rf "$directory"
    mkdir -p "$directory"
    "$dotnet" publish "$bundle/aot/$name/NativeHost.csproj" -c Release -r "$rid" -o "$directory/out" --nologo -v quiet \
        >"$directory/publish.log" 2>&1
}
# The manifest is read on descriptor 3, so that a fixture or a native build that reads standard input (cmd on
# Windows does) cannot consume the fixtures after it.
while IFS=$'\t' read -r name aot_flag arguments environment requires <&3; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    name="$(tr -d '\r' <<<"$name")"
    selected "$name" || continue
    requires="$(tr -d '\r' <<<"${requires:-}")"
    if reason="$(unavailable "$requires")"; then
        if [[ $jit -eq 1 ]]; then record "$name" jit SKIP "$reason"; fi
        if [[ $aot -eq 1 && "$aot_flag" == aot ]]; then record "$name" aot SKIP "$reason"; fi
        continue
    fi
    if [[ $jit -eq 1 ]]; then
        check "$name" jit "$arguments" "$(fixture_environment "$environment" "$requires")" "$dotnet" "$bundle/images/$name.dll" || true
    fi
done 3<"$bundle/manifest.tsv"
if [[ $aot -eq 1 ]]; then
    names=()
    while IFS=$'\t' read -r name aot_flag arguments environment requires <&3; do
        [[ -z "$name" || "$name" == \#* ]] && continue
        name="$(tr -d '\r' <<<"$name")"
        if [[ "$aot_flag" == aot ]] && selected "$name" && ! unavailable "$(tr -d '\r' <<<"${requires:-}")" >/dev/null; then names+=("$name"); fi
    done 3<"$bundle/manifest.tsv"
    printf 'Publishing %d NativeAOT projects for %s (%d at once)\n' "${#names[@]}" "$rid" "$jobs"
    pids=()
    for name in ${names[@]+"${names[@]}"}; do
        publish "$name" &
        pids+=($!)
        if [[ ${#pids[@]} -ge $jobs ]]; then wait "${pids[0]}" || true; pids=(${pids[@]:1}); fi
    done
    wait
    while IFS=$'\t' read -r name aot_flag arguments environment requires <&3; do
        [[ -z "$name" || "$name" == \#* ]] && continue
        name="$(tr -d '\r' <<<"$name")"
        [[ "$aot_flag" == aot ]] && selected "$name" || continue
        unavailable "$(tr -d '\r' <<<"${requires:-}")" >/dev/null && continue
        executable="$results/$name-publish/out/program$exe"
        if [[ ! -x "$executable" ]]; then
            record "$name" aot FAIL "publish failed ($results/$name-publish/publish.log)"
            tail -20 "$results/$name-publish/publish.log"
            continue
        fi
        check "$name" aot "$arguments" "$(fixture_environment "$environment" "$(tr -d '\r' <<<"${requires:-}")")" "$executable" || true
    done 3<"$bundle/manifest.tsv"
fi
printf '%s: %d passed, %d failed, %d skipped (%s)\n' "$host" "$passed" "$failed" "$skipped" "$results/results.tsv" | tee -a "$results/host.txt"
[[ $failed -eq 0 ]]
