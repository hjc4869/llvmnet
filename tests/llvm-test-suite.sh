#!/usr/bin/env bash
# tags: conformance c cpp
# requires: python3 clang++
# timeout: 14400
# WG-01 and WG-02: llvm-test-suite's SingleSource and MultiSource programs (C and C++; gcc-c-torture's execute tests
# are SingleSource/Regression/C/gcc-c-torture) built through the suite's own CMake with llvmnet-clang and
# llvmnet-clang++ (cmake/llvmnet.cmake) and run by its lit configuration against its reference outputs. The same
# directories are built natively with clang-22 against glibc; a test counts when the native build passes it.
#   tests/llvm-test-suite.sh [portable] [system] [aot]   modes (default: portable system aot); aot is portable NativeAOT
#                                                        on the LLVM_TEST_SUITE_AOT directories only
#   LLVM_TEST_SUITE_DIRS="SingleSource;MultiSource"      the suite directories to build (default)
#   LLVM_TEST_SUITE_AOT="SingleSource/Benchmarks"        the NativeAOT subset (default)
#   LLVM_TEST_SUITE_JOBS=12                              build and lit jobs (default: 12; NativeAOT uses a quarter)
#   LLVM_TEST_SUITE_THRESHOLD=99                         fail below this pass rate in a mode, counted without the
#                                                        failures tests/conformance/llvm-test-suite.tsv classifies as
#                                                        not applicable or documented exclusions (default 0: report
#                                                        only); any unclassified failure fails the test
#   LLVMNET_SYSTEM_CXX=<archive>                         system-mode libstdc++ archive (default: artifacts/system-cxx/...)
# Results: artifacts/tests/llvm-test-suite/{summary.tsv,results.tsv,failures.txt}; the summary has one row per mode
# and per group (gcc-c-torture, the other SingleSource directories, MultiSource/Applications and Benchmarks).
# On a shared machine run it in a memory cap: systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0 ...
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
suite="$("$root/scripts/fetch-sources.sh" --path llvm-test-suite)"
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
lit="$llvm/llvm/utils/lit/lit.py"
modes=("$@")
[[ ${#modes[@]} -gt 0 ]] || modes=(portable system aot)
directories="${LLVM_TEST_SUITE_DIRS:-SingleSource;MultiSource}"
aot_directories="${LLVM_TEST_SUITE_AOT:-SingleSource/Benchmarks}"
jobs="${LLVM_TEST_SUITE_JOBS:-12}"
rm -rf "$test_output"
mkdir -p "$test_output/bin"
# The compilers the suite's CMake calls: llvmnet's C and C++ drivers in one mode each. System-mode C++ links the
# optional libstdc++ bitcode archive (docs/user/modes.md) when it has been built; without it the C++ programs do not link.
system_cxx="${LLVMNET_SYSTEM_CXX:-$root/artifacts/system-cxx/libstdcxx-system.a}"
# Portable code sees neither __linux__ nor __unix__ (ADR-14). Programs that choose their platform code by those macros
# build with them as a per-program setting, as the SPEC benchmarks do (docs/dev/spec.md), listed in the classification
# file's "platform" rows: the wrapper adds them when it compiles a source below one of those directories.
classes="$root/tests/conformance/llvm-test-suite.tsv"
platform_case='case " $* " in'
while IFS=$'\t' read -r kind directory _; do
    [[ "$kind" == platform ]] && platform_case+=" *\"/$directory/\"*) flags=\"-D__linux__ -D__unix__ -Dlinux -Dunix\" ;;"
done < <(grep -v '^#' "$classes")
platform_case+=' esac'
for mode in "${modes[@]}"; do
    runtime="$mode" link=""
    if [[ "$mode" == aot ]]; then runtime=portable link=" --nativeaot"; fi
    for driver in clang clang++; do
        if [[ "$mode" == system && "$driver" == clang++ && -f "$system_cxx" ]]; then link=" $system_cxx"; fi
        platform=""
        if [[ "$runtime" == portable ]]; then platform="$platform_case"; fi
        # shellcheck disable=SC2016  # $* and $extra belong to the generated wrapper
        printf '#!/bin/sh\ncase " $* " in *" -c "*|*" -E "*) extra= ;; *) extra="%s" ;; esac\nflags=\n%s\nLLVMNET_DRIVER=%s exec %q --runtime=%s $flags "$@" $extra\n' \
            "$link" "$platform" "$driver" "$llvmnet" "$runtime" >"$test_output/bin/$mode-$driver"
        chmod +x "$test_output/bin/$mode-$driver"
    done
done
configure() {
    local build="$1" dirs="$2"
    shift 2
    cmake -G Ninja -S "$suite" -B "$build" -DCMAKE_BUILD_TYPE=Release -DTEST_SUITE_SUBDIRS="$dirs" \
        -DTEST_SUITE_LIT="$lit" -DTEST_SUITE_COLLECT_CODE_SIZE=OFF -DTEST_SUITE_COLLECT_COMPILE_TIME=OFF "$@" \
        >"$build.configure.log" 2>&1
}
# build_and_run <name> <jobs> [<subset directory>...]: builds and runs everything, or only the given directories and
# the suite's tools. The suite is always configured from its top directories, whose lit.local.cfg files CMake copies
# into the build tree (SingleSource's says how outputs are compared).
build_and_run() {
    local name="$1" build_jobs="$2"
    shift 2
    local build="$test_output/$name" targets=() paths=("$test_output/$name") subset
    if [[ $# -gt 0 ]]; then
        targets=(tools/all) paths=()
        for subset in "$@"; do targets+=("$subset/all"); paths+=("$build/$subset"); done
    fi
    # A program that does not build fails its lit test; the build log names the reason.
    ninja -C "$build" -k 0 -j "$build_jobs" "${targets[@]}" >"$build.build.log" 2>&1 || true
    python3 "$lit" -q -j "$build_jobs" --timeout 900 --max-failures 100000 -o "$build.json" "${paths[@]}" >"$build.lit.log" 2>&1 || true
}
configure "$test_output/native" "$directories" -DCMAKE_C_COMPILER=clang-22 -DCMAKE_CXX_COMPILER=clang++-22
build_and_run native "$jobs"
for mode in "${modes[@]}"; do
    build_jobs="$jobs" subsets=()
    if [[ "$mode" == aot ]]; then
        build_jobs=$(( (jobs + 3) / 4 ))
        IFS=';' read -r -a subsets <<<"$aot_directories"
    fi
    # The programs run directly (an llvmnet executable is a launcher), so the suite's tools are host builds. The suite
    # selects directories by architecture and system, which it detects by reading strings out of a compiled file: state
    # them. System mode is x86-64 Linux; the portable ABI has x86-64's data model (LP64, x87 long double, signed char)
    # and Linux system-call semantics, but no __x86_64__, so x86 intrinsic tests do not apply to it.
    configure "$test_output/$mode" "$directories" -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" -DARCH=x86 -DTARGET_OS=Linux \
        -DCMAKE_C_COMPILER="$test_output/bin/$mode-clang" -DCMAKE_CXX_COMPILER="$test_output/bin/$mode-clang++" \
        -DTEST_SUITE_USER_MODE_EMULATION=ON -DTEST_SUITE_HOST_CC=clang-22
    build_and_run "$mode" "$build_jobs" ${subsets[@]+"${subsets[@]}"}
done
python3 - "$test_output" "${LLVM_TEST_SUITE_THRESHOLD:-0}" "$classes" "${modes[@]}" <<'PYEOF'
import fnmatch, json, os, re, sys

output, threshold, classes_file, modes = sys.argv[1], float(sys.argv[2]), sys.argv[3], sys.argv[4:]
# Classified failures: "fail <mode glob> <test glob> <class> <reason>" rows. Classes ending in "not-applicable" or
# starting with "exclusion" are left out of the adjusted pass rate; every other failure counts in both rates.
classified = [line.rstrip('\n').split('\t') for line in open(classes_file) if line.startswith('fail\t')]
def classify(mode, test):
    return next((row[3] for row in classified if fnmatch.fnmatch(mode, row[1]) and fnmatch.fnmatch(test, row[2])), 'unclassified')
def excluded(name): return name.endswith('not-applicable') or name.startswith('exclusion')

def results(name):
    path = f'{output}/{name}.json'
    if not os.path.exists(path): return {}
    return {t['name'].split(' :: ', 1)[1][:-len('.test')]: t['code'] for t in json.load(open(path))['tests']}

def build_errors(name):
    """The first llvmnet or compiler error of every target that failed to build, keyed by its output path."""
    errors, target = {}, None
    for line in open(f'{output}/{name}.build.log', errors='replace'):
        if m := re.match(r'FAILED: (?:\[code=\d+\] )?(\S+)', line): target = m.group(1)
        elif target and ('error:' in line or 'Error' in line) and target not in errors:
            errors[target] = re.sub(r'^.*?error: ', '', line.strip())[:160]
    return errors

def group(test):
    if '/gcc-c-torture/' in test: return 'gcc-c-torture'
    parts = test.split('/')
    return '/'.join(parts[:2])

native = results('native')
rows = []
for mode in modes:
    managed, errors = results(mode), build_errors(mode)
    for test, code in sorted(managed.items()):
        if native.get(test) != 'PASS':
            rows.append((mode, group(test), test, 'native-fails', native.get(test, 'not built natively')))
            continue
        if code == 'PASS':
            rows.append((mode, group(test), test, 'pass', ''))
            continue
        program = os.path.basename(test)
        reason = next((f'build: {e}' for t, e in errors.items() if os.path.basename(t) == program), None)
        rows.append((mode, group(test), test, 'fail', reason or code.lower()))
rows = [row + (classify(row[0], row[2]) if row[3] == 'fail' else '',) for row in rows]
with open(f'{output}/results.tsv', 'w') as table:
    for row in rows: table.write('\t'.join(row) + '\n')
open(f'{output}/failures.txt', 'w').write(''.join(f'{m}\t{t}\t{c}\t{r}\n' for m, g, t, s, r, c in rows if s == 'fail'))
low = []
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('mode\tgroup\tcounted\tpassed\tfailed\tnative failures (not counted)\tpass rate\t'
                  'excluded (not applicable, documented exclusions)\tpass rate without them\tunclassified failures\n')
    for mode in modes:
        mode_rows = [r for r in rows if r[0] == mode]
        for name in sorted({r[1] for r in mode_rows}) + ['all']:
            selected = [r for r in mode_rows if name == 'all' or r[1] == name]
            passed = sum(r[3] == 'pass' for r in selected); failed = sum(r[3] == 'fail' for r in selected)
            skipped = sum(r[3] == 'native-fails' for r in selected)
            left_out = sum(r[3] == 'fail' and excluded(r[5]) for r in selected)
            unknown = sum(r[3] == 'fail' and r[5] == 'unclassified' for r in selected)
            rate = 100.0 * passed / (passed + failed) if passed + failed else 100.0
            adjusted = 100.0 * passed / (passed + failed - left_out) if passed + failed - left_out else 100.0
            summary.write(f'{mode}\t{name}\t{passed + failed}\t{passed}\t{failed}\t{skipped}\t{rate:.1f}\t{left_out}\t{adjusted:.1f}\t{unknown}\n')
            print(f'{mode} {name}: {passed} of {passed + failed} pass ({rate:.1f}%), {adjusted:.1f}% without {left_out} '
                  f'not applicable or excluded; {unknown} unclassified; {skipped} fail natively and are not counted')
            if name == 'all' and (adjusted < threshold or unknown or not mode_rows): low.append(mode)
        counts = {}
        for r in mode_rows:
            if r[3] == 'fail': counts[r[5]] = counts.get(r[5], 0) + 1
        for name, count in sorted(counts.items()): print(f'  {mode} failures classified {name}: {count}')
for mode, _, test, status, reason, kind in rows:
    if status == 'fail': print(f'  FAIL {mode} {test} [{kind}]: {reason}')
if low:
    print(f'FAIL: below {threshold}% without exclusions, unclassified failures, or no results: {" ".join(low)}', file=sys.stderr)
    sys.exit(1)
print('PASS: llvm-test-suite')
PYEOF
