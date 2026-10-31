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
while IFS=$'\t' read -r name aot_flag arguments environment; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    name="$(tr -d '\r' <<<"$name")"
    selected "$name" || continue
    if [[ $jit -eq 1 ]]; then
        check "$name" jit "$arguments" "$environment" "$dotnet" "$bundle/images/$name.dll" || true
    fi
done <"$bundle/manifest.tsv"
if [[ $aot -eq 1 ]]; then
    names=()
    while IFS=$'\t' read -r name aot_flag arguments environment; do
        [[ -z "$name" || "$name" == \#* ]] && continue
        name="$(tr -d '\r' <<<"$name")"
        if [[ "$aot_flag" == aot ]] && selected "$name"; then names+=("$name"); fi
    done <"$bundle/manifest.tsv"
    printf 'Publishing %d NativeAOT projects for %s (%d at once)\n' "${#names[@]}" "$rid" "$jobs"
    pids=()
    for name in ${names[@]+"${names[@]}"}; do
        publish "$name" &
        pids+=($!)
        if [[ ${#pids[@]} -ge $jobs ]]; then wait "${pids[0]}" || true; pids=(${pids[@]:1}); fi
    done
    wait
    while IFS=$'\t' read -r name aot_flag arguments environment; do
        [[ -z "$name" || "$name" == \#* ]] && continue
        name="$(tr -d '\r' <<<"$name")"
        [[ "$aot_flag" == aot ]] && selected "$name" || continue
        executable="$results/$name-publish/out/program$exe"
        if [[ ! -x "$executable" ]]; then
            record "$name" aot FAIL "publish failed ($results/$name-publish/publish.log)"
            tail -20 "$results/$name-publish/publish.log"
            continue
        fi
        check "$name" aot "$arguments" "$environment" "$executable" || true
    done <"$bundle/manifest.tsv"
fi
printf '%s: %d passed, %d failed, %d skipped (%s)\n' "$host" "$passed" "$failed" "$skipped" "$results/results.tsv" | tee -a "$results/host.txt"
[[ $failed -eq 0 ]]
