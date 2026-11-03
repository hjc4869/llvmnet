#!/usr/bin/env bash
# llvmnet test runner. Discovers tests/*.sh scripts that declare a "# tags:" header, builds once,
# runs them in parallel with per-test logs, continues after failures, and writes
# artifacts/tests/results/{summary.tsv,results.xml,<test>.log}.
#
# Usage: tests/run.sh [options] [test-name...]
#   --tag T            select tests with tag T (repeatable; default: core)
#   --exclude-tag T    drop tests with tag T (repeatable)
#   -j, --jobs N       parallel jobs for the parallel phase (default: LLVMNET_TEST_JOBS or min(nproc/4, 8))
#   --list             print the selected tests with tags and prerequisites, then exit
#   --no-build         use the existing build
#   --timeout-scale F  multiply every per-test timeout by F (for slow machines)
#
# Test header keys (comment lines within the first 15 lines of each script):
#   # tags: core cpp aot        selection tags
#   # requires: flang openmp    prerequisites; missing ones make the test SKIP with a reason
#   # serial: 20                run alone, after the parallel phase, ordered by this number
#   # after: codecs             serial tests only: skip when a selected prerequisite did not pass in this run
#   # timeout: 1800             seconds (default 1200)
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
results="$root/artifacts/tests/results"
tags=() excluded=() names=()
jobs="${LLVMNET_TEST_JOBS:-}"
list=0 build=1 scale=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag) tags+=("$2"); shift 2 ;;
        --tag=*) tags+=("${1#*=}"); shift ;;
        --exclude-tag) excluded+=("$2"); shift 2 ;;
        --exclude-tag=*) excluded+=("${1#*=}"); shift ;;
        -j|--jobs) jobs="$2"; shift 2 ;;
        -j*) jobs="${1#-j}"; shift ;;
        --list) list=1; shift ;;
        --no-build) build=0; shift ;;
        --timeout-scale) scale="$2"; shift 2 ;;
        -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) printf 'Unknown option %s\n' "$1" >&2; exit 2 ;;
        *) names+=("$1"); shift ;;
    esac
done
if [[ -z "$jobs" ]]; then jobs=$(( $(nproc) / 4 )); (( jobs < 1 )) && jobs=1; (( jobs > 8 )) && jobs=8; fi
[[ ${#tags[@]} -eq 0 && ${#names[@]} -eq 0 ]] && tags=(core)

declare -A t_tags t_requires t_serial t_after t_timeout
all=()
for script in "$root"/tests/*.sh; do
    name="$(basename "$script" .sh)"
    header="$(sed -n '1,15p' "$script")"
    line="$(grep -m1 '^# tags:' <<<"$header")" || continue
    all+=("$name")
    t_tags[$name]="${line#\# tags:}"
    line="$(grep -m1 '^# requires:' <<<"$header")" && t_requires[$name]="${line#\# requires:}" || t_requires[$name]=""
    line="$(grep -m1 '^# serial:' <<<"$header")" && t_serial[$name]="$(tr -d ' ' <<<"${line#\# serial:}")" || t_serial[$name]=""
    line="$(grep -m1 '^# after:' <<<"$header")" && t_after[$name]="${line#\# after:}" || t_after[$name]=""
    line="$(grep -m1 '^# timeout:' <<<"$header")" && t_timeout[$name]="$(tr -d ' ' <<<"${line#\# timeout:}")" || t_timeout[$name]=1200
done

has_tag() { local name="$1" tag="$2" t; for t in ${t_tags[$name]}; do [[ "$t" == "$tag" ]] && return 0; done; return 1; }
selected=()
if [[ ${#names[@]} -gt 0 ]]; then
    for name in "${names[@]}"; do
        if [[ -z "${t_tags[$name]+x}" ]]; then printf 'Unknown test %s (no tests/%s.sh with a "# tags:" header)\n' "$name" "$name" >&2; exit 2; fi
        selected+=("$name")
    done
else
    for name in "${all[@]}"; do
        for tag in "${tags[@]}"; do if has_tag "$name" "$tag"; then selected+=("$name"); break; fi; done
    done
fi
filtered=()
for name in "${selected[@]}"; do
    drop=0
    for tag in "${excluded[@]}"; do has_tag "$name" "$tag" && drop=1; done
    [[ "$drop" -eq 0 ]] && filtered+=("$name")
done
selected=("${filtered[@]}")

missing() {
    local requirement
    for requirement in ${t_requires[$1]}; do
        case "$requirement" in
            flang) command -v "${LLVMNET_FLANG:-flang-22}" >/dev/null || { echo "flang-22 not found"; return; } ;;
            clang++) command -v clang++-22 >/dev/null || { echo "clang++-22 not found"; return; } ;;
            perl) command -v perl >/dev/null || { echo "perl not found"; return; } ;;
            node) command -v node >/dev/null || { echo "node not found"; return; } ;;
            wasm-ld) [[ -n "${WASM_LD:-}" ]] || command -v wasm-ld-22 >/dev/null || command -v wasm-ld >/dev/null || command -v wasm-ld-21 >/dev/null || { echo "no wasm-ld (install lld-22 or set WASM_LD)"; return; } ;;
            python3) command -v python3 >/dev/null || { echo "python3 not found"; return; } ;;
            egl)
                local libraries
                libraries="$(ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null)"
                if ! grep -q 'libEGL\.so\.1 ' <<<"$libraries" || ! grep -q 'libGLESv2\.so\.2 ' <<<"$libraries"; then
                    echo "libEGL.so.1 or libGLESv2.so.2 not found (install libegl1, libgles2 and Mesa: scripts/ci/install-packages.sh)"; return
                fi ;;
            vulkan)
                local loader
                loader="$(ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null)"
                if ! grep -q 'libvulkan\.so\.1 ' <<<"$loader" || [[ ! -f /usr/share/vulkan/icd.d/lvp_icd.json ]]; then
                    echo "libvulkan.so.1 or Mesa's lavapipe not found (install libvulkan1 and mesa-vulkan-drivers: scripts/ci/install-packages.sh)"; return
                fi ;;
            rsync) command -v rsync >/dev/null || { echo "rsync not found (needed to fetch the FATE samples)"; return; } ;;
            cmake) { command -v cmake && command -v ninja; } >/dev/null || { echo "cmake or ninja not found"; return; } ;;
            fuzz-build) { command -v cmake && command -v m4 && command -v clang++-22; } >/dev/null || { echo "cmake, m4 or clang++-22 not found (needed to build csmith and yarpgen)"; return; } ;;
            ffmpeg-cli) command -v ffmpeg >/dev/null || { echo "ffmpeg command-line tool not found (needed to generate media fixtures)"; return; } ;;
            openmp) [[ -f "${LLVMNET_OPENMP_PREFIX:-$root/artifacts/system-openmp}/lib/libomp.so.5" ]] || { echo "system OpenMP not staged; run scripts/install-system-openmp.sh"; return; } ;;
            system-cxx) [[ -f "${LLVMNET_SYSTEM_CXX:-$root/artifacts/system-cxx/libstdcxx-system.a}" ]] || { echo "system libstdc++ archive not built; see docs/user/modes.md"; return; } ;;
            local-media) [[ -n "${FFMPEG_TEST_VIDEO:-}" && -f "${FFMPEG_TEST_VIDEO:-}" ]] || { echo "FFMPEG_TEST_VIDEO does not name a local video file"; return; } ;;
            *) echo "unknown requirement $requirement"; return ;;
        esac
    done
}

if [[ "$list" -eq 1 ]]; then
    printf '%-28s %-8s %-34s %s\n' TEST PHASE TAGS REQUIRES
    for name in "${selected[@]}"; do
        phase=parallel; [[ -n "${t_serial[$name]}" ]] && phase="serial${t_serial[$name]}"
        printf '%-28s %-8s %-34s %s\n' "$name" "$phase" "$(xargs <<<"${t_tags[$name]}")" "$(xargs <<<"${t_requires[$name]}")"
    done
    exit 0
fi
if [[ ${#selected[@]} -eq 0 ]]; then printf 'No tests selected.\n' >&2; exit 2; fi

rm -rf "$results"
mkdir -p "$results/status"
touch "$results/started"   # tests/jit-validation.sh checks only assemblies built after this
start_all=$(date +%s)
if [[ "$build" -eq 1 ]]; then
    printf 'Building solution and portable runtime...\n'
    if ! bash "$root/scripts/ensure-built.sh" >"$results/build.log" 2>&1; then
        cat "$results/build.log" >&2
        printf 'FAIL: build failed; no tests were run. Log: %s\n' "$results/build.log" >&2
        exit 1
    fi
fi
export LLVMNET_SKIP_BUILD=1 LLVMNET_TEST_RUNNER=1

record() { printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" >"$results/status/$1"; }
run_one() {
    local name="$1" reason limit begin status code
    reason="$(missing "$name")"
    if [[ -n "$reason" ]]; then record "$name" SKIP 0 "$reason"; return; fi
    limit="$(awk -v t="${t_timeout[$name]}" -v s="$scale" 'BEGIN { printf "%d", t * s }')"
    begin=$(date +%s)
    ( cd "$root" && exec timeout --kill-after=30 "$limit" bash "$root/tests/$name.sh" ) >"$results/$name.log" 2>&1 </dev/null
    code=$?
    case "$code" in
        0) status=PASS; reason="" ;;
        124|137) status=TIMEOUT; reason="exceeded ${limit}s" ;;
        *) status=FAIL; reason="exit $code; last line: $(tail -n 1 "$results/$name.log" | tr '\t' ' ' | cut -c1-200)" ;;
    esac
    record "$name" "$status" "$(( $(date +%s) - begin ))" "$reason"
}
report() {
    local name="$1" status seconds reason
    IFS=$'\t' read -r _ status seconds reason <"$results/status/$name"
    printf '%-8s %-28s %5ss %s\n' "$status" "$name" "$seconds" "$reason"
}

parallel=() serial=()
for name in "${selected[@]}"; do
    if [[ -n "${t_serial[$name]}" ]]; then serial+=("$name"); else parallel+=("$name"); fi
done
printf 'Running %d tests (%d parallel with %d jobs, %d serial).\n' "${#selected[@]}" "${#parallel[@]}" "$jobs" "${#serial[@]}"
declare -A pids=()
for name in "${parallel[@]}"; do
    while [[ ${#pids[@]} -ge "$jobs" ]]; do
        wait -n 2>/dev/null
        for pid in "${!pids[@]}"; do
            if ! kill -0 "$pid" 2>/dev/null; then report "${pids[$pid]}"; unset "pids[$pid]"; fi
        done
    done
    run_one "$name" &
    pids[$!]="$name"
done
while [[ ${#pids[@]} -gt 0 ]]; do
    wait -n 2>/dev/null
    for pid in "${!pids[@]}"; do
        if ! kill -0 "$pid" 2>/dev/null; then report "${pids[$pid]}"; unset "pids[$pid]"; fi
    done
done
if [[ ${#serial[@]} -gt 0 ]]; then
    mapfile -t serial < <(for name in "${serial[@]}"; do printf '%s %s\n' "${t_serial[$name]}" "$name"; done | sort -n -k1,1 -k2,2 | cut -d' ' -f2)
    for name in "${serial[@]}"; do
        blocked=""
        for dependency in ${t_after[$name]}; do
            if [[ -f "$results/status/$dependency" ]] && ! grep -q $'\tPASS\t' "$results/status/$dependency"; then blocked="$dependency did not pass"; fi
        done
        if [[ -n "$blocked" ]]; then record "$name" SKIP 0 "$blocked"; else run_one "$name"; fi
        report "$name"
    done
fi

{
    printf 'test\tstatus\tseconds\treason\n'
    for name in "${selected[@]}"; do cat "$results/status/$name"; done
} >"$results/summary.tsv"
xml() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'; }
count() { awk -F '\t' -v s="$1" 'NR > 1 && $2 == s' "$results/summary.tsv" | wc -l; }
passed=$(count PASS) failed=$(count FAIL) timed=$(count TIMEOUT) skipped=$(count SKIP)
total_seconds=$(( $(date +%s) - start_all ))
{
    printf '<?xml version="1.0" encoding="UTF-8"?>\n'
    printf '<testsuite name="llvmnet" tests="%d" failures="%d" errors="%d" skipped="%d" time="%d">\n' \
        "${#selected[@]}" "$failed" "$timed" "$skipped" "$total_seconds"
    tail -n +2 "$results/summary.tsv" | while IFS=$'\t' read -r name status seconds reason; do
        printf '  <testcase classname="llvmnet.tests" name="%s" time="%s">' "$name" "$seconds"
        case "$status" in
            FAIL) printf '<failure message="%s"><![CDATA[%s]]></failure>' "$(xml <<<"$reason")" "$(tail -n 60 "$results/$name.log" | sed 's/]]>/]] >/g')" ;;
            TIMEOUT) printf '<error message="%s"/>' "$(xml <<<"$reason")" ;;
            SKIP) printf '<skipped message="%s"/>' "$(xml <<<"$reason")" ;;
        esac
        printf '</testcase>\n'
    done
    printf '</testsuite>\n'
} >"$results/results.xml"
rm -rf "$results/status"
printf '\n%d passed, %d failed, %d timed out, %d skipped in %ds. Results: %s\n' \
    "$passed" "$failed" "$timed" "$skipped" "$total_seconds" "$results"
[[ "$failed" -eq 0 && "$timed" -eq 0 ]]
