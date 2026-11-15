#!/usr/bin/env bash
# Runs the portable-toolchain kit (make-kit.sh) on this host with a portable llvmnet toolchain (XP-04): compiles every
# fixture with the toolchain's llvmnet-clang, llvmnet-clang++ or llvmnet-flang, runs the image with dotnet and compares
# its output and exit status with the native Linux build's; then checks a launcher output, separate compilation with an
# archive, Fortran modules across files and, on Windows, the .cmd commands. Works on Linux, macOS and Windows (Git Bash).
#
# Usage: run.sh [--jobs <n>] [--only <names>] <kit-dir> <toolchain-dir>
# Needs bash and the .NET 10 runtime (the toolchain finds it as its bin/llvmnet does). Results: <kit>/results/<host>/
# (results.tsv: name, result, compile seconds, note; one directory per fixture). Written for bash 3.2 (macOS).
set -o pipefail
jobs=2 only="" kit="" toolchain=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --jobs) jobs="$2"; shift 2 ;;
        --only) only="$2"; shift 2 ;;
        -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) printf 'Unknown option %s\n' "$1" >&2; exit 2 ;;
        *) if [[ -z "$kit" ]]; then kit="$1"; else toolchain="$1"; fi; shift ;;
    esac
done
if [[ -z "$kit" || -z "$toolchain" ]]; then printf 'Usage: run.sh [--jobs <n>] [--only <names>] <kit-dir> <toolchain-dir>\n' >&2; exit 2; fi
kit="$(cd "$kit" && pwd)"
toolchain="$(cd "$toolchain" && pwd)"
if [[ ! -f "$kit/manifest.tsv" ]]; then printf '%s is not a kit (no manifest.tsv)\n' "$kit" >&2; exit 2; fi
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) system=windows ;;
    Darwin) system=macos ;;
    *) system="$(uname -s | tr '[:upper:]' '[:lower:]')" ;;
esac
host="$system-$(uname -m | tr '[:upper:]' '[:lower:]')"
results="$kit/results/$host"
rm -rf "$results"
mkdir -p "$results"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ulimit -S -s 8192 2>/dev/null || true
bin="$toolchain/bin"
# The dotnet that runs the images: the one the toolchain's command finds.
dotnet="${LLVMNET_DOTNET:-}"
if [[ -z "$dotnet" ]]; then
    if [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then dotnet="$DOTNET_ROOT/dotnet"; else dotnet=dotnet; fi
fi
{
    printf 'host %s\n' "$(uname -a)"
    printf 'dotnet %s\n' "$("$dotnet" --version 2>/dev/null || echo "runtime only")"
    printf 'toolchain %s\n' "$("$bin/llvmnet" --version 2>&1 | head -1)"
    cat "$kit/VERSION" 2>/dev/null
} | tee "$results/host.txt"
selected() { [[ -z "$only" ]] || [[ ",$only," == *",$1,"* ]]; }

# fixture <name> <sources> <flags> <arguments> <environment>: compile, run, compare; writes <results>/<name>/result.
fixture() {
    local name="$1" sources="$2" flags="$3" arguments="$4" environment="$5"
    local directory="$results/$name" driver start finished status expected source
    mkdir -p "$directory/work"
    case "${sources%% *}" in
        *.c) driver=llvmnet-clang ;;
        *.cpp) driver=llvmnet-clang++ ;;
        *) driver=llvmnet-flang ;;
    esac
    local paths=()
    for source in $sources; do paths+=("$kit/src/$source"); done
    start=$(date +%s)
    # shellcheck disable=SC2086
    if ! "$bin/$driver" $flags "${paths[@]}" -o "$directory/$name.dll" >"$directory/compile.log" 2>&1; then
        printf 'FAIL\t%s\tcompile failed: %s\n' "$(( $(date +%s) - start ))" "$(grep -m1 -i error "$directory/compile.log" | cut -c1-200)" >"$directory/result"
        return
    fi
    finished=$(( $(date +%s) - start ))
    local run_arguments=() run_environment=()
    # shellcheck disable=SC2206
    if [[ "$arguments" != - ]]; then run_arguments=($arguments); fi
    if [[ "$environment" != - ]]; then IFS=';' read -r -a run_environment <<<"$environment"; fi
    status=0
    (cd "$directory/work" && env ${run_environment[@]+"${run_environment[@]}"} "$dotnet" "$directory/$name.dll" ${run_arguments[@]+"${run_arguments[@]}"} \
        >"$directory/stdout.txt" 2>"$directory/stderr.txt") || status=$?
    expected="$(cat "$kit/expected/$name.status")"
    if [[ "$status" != "$expected" ]]; then
        printf 'FAIL\t%s\texit status %s, native %s\n' "$finished" "$status" "$expected" >"$directory/result"
    elif ! diff "$kit/expected/$name.txt" "$directory/stdout.txt" >"$directory/stdout.diff" 2>&1 &&
        ! diff --strip-trailing-cr "$kit/expected/$name.txt" "$directory/stdout.txt" >/dev/null 2>&1; then
        printf 'FAIL\t%s\toutput differs (stdout.diff)\n' "$finished" >"$directory/result"
    else
        printf 'PASS\t%s\t\n' "$finished" >"$directory/result"
        rm -rf "$directory/work"
    fi
}

names=()
running=0
while IFS=$'\t' read -r name sources flags arguments environment; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    selected "$name" || continue
    names+=("$name")
    fixture "$name" "$sources" "$flags" "$arguments" "$environment" &
    running=$((running + 1))
    if [[ "$running" -ge "$jobs" ]]; then wait; running=0; fi
done <"$kit/manifest.tsv"
wait

passed=0 failed=0
printf '# name\tresult\tcompile seconds\tnote\n' >"$results/results.tsv"
record() {
    printf '%s\t%s\n' "$1" "$2" >>"$results/results.tsv"
    printf '%-26s %s\n' "$1" "$(printf '%s' "$2" | tr '\t' ' ')"
    case "$2" in PASS*) passed=$((passed + 1)) ;; *) failed=$((failed + 1)) ;; esac
}
for name in ${names[@]+"${names[@]}"}; do record "$name" "$(cat "$results/$name/result" 2>/dev/null || printf 'FAIL\t\tno result')"; done

# Toolchain checks beyond single-file compiles, in a scratch directory with relative paths, as a build system works.
checks="$results/toolchain-checks"
mkdir -p "$checks"
check() {
    local name="$1"
    shift
    if (cd "$checks" && "$@") >"$checks/$name.log" 2>&1; then record "$name" "PASS	"; else record "$name" "FAIL		see toolchain-checks/$name.log"; fi
}
selected launcher-output && check launcher-output sh -c '
    printf "#include <stdio.h>\nint main(int c, char **v) { const char *n = v[0]; for (const char *p = n; *p; p++) if (*p == 47) n = p + 1; printf(\"%%s %%d\", n, c); putchar(10); return 3; }\n" >launcher.c &&
    "$0/llvmnet-clang" -O1 launcher.c -o launcher && test -f launcher.llvmnet/program.dll &&
    if [ "$1" = windows ]; then out="$(cmd //c "launcher.cmd a b")"; else out="$(./launcher a b)"; fi; status=$?
    out="$(printf "%s" "$out" | tr -d "\r")"
    echo "$out status $status"; test "$out" = "launcher 3" && test "$status" = 3 &&
    out="$("$0/llvmnet" --run-launcher launcher a b | tr -d "\r")"; status=$?
    echo "run-launcher: $out"; test "$out" = "launcher 3"' "$bin" "$system"
selected archive && check archive sh -c '
    printf "int twice(int x) { return 2 * x; }\n" >twice.c &&
    printf "#include <stdio.h>\nint twice(int);\nint main(void) { printf(\"%%d\", twice(21)); putchar(10); return 0; }\n" >main.c &&
    "$0/llvmnet-clang" -O2 -c twice.c -o twice.o && "$0/llvmnet-ar" rcs libtwice.a twice.o && "$0/llvmnet-ranlib" libtwice.a &&
    "$0/llvmnet-nm" libtwice.a | grep -q "T twice" &&
    "$0/llvmnet-clang" main.c -L. -ltwice -o archive.dll && test "$("$1" archive.dll | tr -d "\r")" = 42' "$bin" "$dotnet"
selected fortran-modules && check fortran-modules sh -c '
    printf "module shapes\n  implicit none\ncontains\n  pure real function area(r)\n    real, intent(in) :: r\n    area = 3.0 * r * r\n  end function\nend module\n" >shapes.f90 &&
    printf "program use_shapes\n  use shapes\n  print \"(F6.1)\", area(2.0)\nend program\n" >use.f90 &&
    "$0/llvmnet-flang" -c shapes.f90 -o shapes.o && "$0/llvmnet-flang" use.f90 shapes.o -o modules.dll &&
    test "$("$1" modules.dll | tr -d "\r" | tr -d " ")" = 12.0' "$bin" "$dotnet"
if [[ "$system" == windows ]] && selected cmd-commands; then
    check cmd-commands sh -c '
        printf "#include <stdio.h>\nint main(void) { puts(\"cmd ok\"); return 0; }\n" >cmd.c &&
        cmd //c "$(cygpath -w "$0/llvmnet-clang.cmd") cmd.c -o cmd.dll" && test "$("$1" cmd.dll | tr -d "\r")" = "cmd ok" &&
        cmd //c "$(cygpath -w "$0/llvmnet-ar.cmd") --version" | grep -qi llvm' "$bin" "$dotnet"
fi
printf '%d passed, %d failed on %s; results in %s\n' "$passed" "$failed" "$host" "$results"
[[ "$failed" -eq 0 ]]
