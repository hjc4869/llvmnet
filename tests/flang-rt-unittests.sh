#!/usr/bin/env bash
# tags: conformance fortran cpp
# requires: flang clang++ python3
# WG-03 and FT-06: flang-rt's own gtest unit tests (flang-rt/unittests/Runtime of the pinned llvm-project), built with
# LLVM's googletest without LLVMSupport, natively with clang++-22 against the host's flang-rt of the same release and
# with llvmnet-clang++ in portable mode against the sysroot's flang-rt. Every test runs in its own process, because a
# test that needs fork aborts the whole program in portable mode. A test counts when it passes natively; death tests
# (EXPECT_DEATH and EXPECT_EXIT) and the tests of asynchronous EXECUTE_COMMAND_LINE need fork; they are reported apart
# and left out of the pass rate, as are tests the unit tests do not compile for an unrecognized operating system.
#   FLANG_RT_UNITTESTS_JOBS=16               parallel processes (default: 16)
#   FLANG_RT_UNITTESTS_THRESHOLD=99          fail below this pass rate (default 0: report only)
# Results: artifacts/tests/flang-rt-unittests/{summary.tsv,results.tsv,failures.txt}.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
llvm="$("$root/scripts/fetch-sources.sh" --path llvm-project)"
native_runtime="$(clang-22 -print-resource-dir)/lib/x86_64-pc-linux-gnu/libflang_rt.runtime.a"
[[ -f "$native_runtime" ]] || { printf 'FAIL: the host flang-rt library %s is missing\n' "$native_runtime" >&2; exit 1; }
rm -rf "$test_output"
mkdir -p "$test_output/generated"
sed -e 's/#cmakedefine01 HAVE_STRERROR_R/#define HAVE_STRERROR_R 1/' -e 's/#cmakedefine01 HAVE_DECL_STRERROR_S/#define HAVE_DECL_STRERROR_S 0/' \
    -e '/#cmakedefine HAVE_BACKTRACE/d' -e 's/\${BACKTRACE_HEADER}/execinfo.h/' \
    "$llvm/flang-rt/cmake/config.h.cmake.in" >"$test_output/generated/config.h"
gtest="$llvm/third-party/unittest"
unittests="$llvm/flang-rt/unittests/Runtime"
sources=("$gtest/googletest/src/gtest-all.cc" "$gtest/googlemock/src/gmock-all.cc" "$gtest/googletest/src/gtest_main.cc")
while read -r name; do sources+=("$unittests/$name"); done < <(sed -n '/add_flangrt_unittest(RuntimeTests/,/LINK_LIBS/p' \
    "$unittests/CMakeLists.txt" | grep -o '[A-Za-z]*\.cpp')
# LLVM's unit-test settings: no RTTI, googletest without LLVMSupport. NOT_EXE names a command that fails.
flags=(-std=c++17 -O1 -w -fno-rtti -DGTEST_HAS_RTTI=0 -DGTEST_NO_LLVM_SUPPORT=1 "-DNOT_EXE=\"/bin/false\""
    -I"$gtest/googletest/include" -I"$gtest/googletest" -I"$gtest/googlemock/include" -I"$gtest/googlemock"
    -I"$llvm/flang-rt/include" -I"$llvm/flang/include" -I"$test_output/generated" -I"$llvm/flang-rt/lib/runtime" -I"$unittests"
    -include "$root/tests/support/gtest-without-death-tests.h")
export test_output
build() {
    local kind="$1"
    shift
    mkdir -p "$test_output/$kind/objects"
    printf '%s\n' "${sources[@]}" | xargs -P "${FLANG_RT_UNITTESTS_JOBS:-16}" -I{} bash -c \
        'object="$test_output/'"$kind"'/objects/$(basename "$1").o"; "${@:2}" -c "$1" -o "$object" >"$object.log" 2>&1 || cat "$object.log" >&2' \
        _ {} "$@" "${flags[@]}"
}
build native clang++-22
clang++-22 "$test_output"/native/objects/*.o "$native_runtime" -lpthread -lm -o "$test_output/native/RuntimeTests"
build portable "$llvmnet" --runtime=portable --driver-mode=g++
"$llvmnet" --runtime=portable --driver-mode=g++ "$test_output"/portable/objects/*.o -o "$test_output/portable/RuntimeTests"
python3 - "$test_output" "${FLANG_RT_UNITTESTS_THRESHOLD:-0}" <<'PYEOF'
import concurrent.futures, os, subprocess, sys

output, threshold = sys.argv[1], float(sys.argv[2])
jobs = int(os.environ.get('FLANG_RT_UNITTESTS_JOBS', 16))
listing = subprocess.run([f'{output}/native/RuntimeTests', '--gtest_list_tests'], capture_output=True, text=True, check=True).stdout
tests, suite = [], None
for line in listing.splitlines():
    if not line.startswith(' '): suite = line.split('#')[0].strip()
    else: tests.append(suite + line.split('#')[0].strip())

def run(kind, test):
    directory = os.path.join(output, kind, 'work', test.replace('/', '_'))
    os.makedirs(directory, exist_ok=True)
    try:
        result = subprocess.run([f'{output}/{kind}/RuntimeTests', f'--gtest_filter={test}'], cwd=directory, timeout=300,
                                capture_output=True, text=True, errors='replace', stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        return 'fail', 'timeout'
    text = result.stdout + result.stderr
    if result.returncode == 0 and '[  PASSED  ] 1 test' in text: return 'pass', ''
    # The unit tests compile some tests only for the operating systems they recognize (fdate, getlog) or for REAL(16).
    if result.returncode == 0 and '[  PASSED  ] 0 tests' in text: return 'absent', 'not compiled for this target'
    if 'death test (needs fork)' in text: return 'death', 'death test (needs fork)'
    # Asynchronous EXECUTE_COMMAND_LINE forks, which portable mode does not provide (fork stays ENOSYS).
    if 'Fork fail' in text: return 'death', 'needs fork'
    reason = next((line.strip() for line in text.splitlines() if 'fatal Fortran runtime error' in line or 'Failure' in line
                   or line.startswith('Unhandled exception')), f'exit {result.returncode}')
    return 'fail', reason[:160]

def check(test):
    native, _ = run('native', test)
    if native != 'pass': return test, 'native-fails', ''
    status, reason = run('portable', test)
    return test, status, reason

with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
    results = list(pool.map(check, tests))
with open(f'{output}/results.tsv', 'w') as table:
    for row in results: table.write('\t'.join(row) + '\n')
open(f'{output}/failures.txt', 'w').write(''.join(f'{t}\t{r}\n' for t, s, r in results if s in ('fail', 'death')))
passed = sum(s == 'pass' for _, s, _ in results); failed = sum(s == 'fail' for _, s, _ in results)
death = sum(s == 'death' for _, s, _ in results); native = sum(s == 'native-fails' for _, s, _ in results)
absent = sum(s == 'absent' for _, s, _ in results)
rate = 100.0 * passed / (passed + failed) if passed + failed else 100.0
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('tests\tpassed\tfailed\tneed fork (death tests, asynchronous commands)\tnot compiled for the portable target\t'
                  'native failures (not counted)\tpass rate without death tests\n')
    summary.write(f'{len(results)}\t{passed}\t{failed}\t{death}\t{absent}\t{native}\t{rate:.1f}\n')
print(f'flang-rt unit tests in portable mode: {passed} passed, {failed} failed, {death} need fork (death tests, asynchronous commands), '
      f'{absent} not compiled for the portable target, {native} fail natively and are not counted ({rate:.1f}% without death tests)')
for test, status, reason in results:
    if status == 'fail': print(f'  FAIL {test}: {reason}')
if rate < threshold:
    print(f'FAIL: below {threshold}%', file=sys.stderr)
    sys.exit(1)
print('PASS: flang-rt unit tests')
PYEOF
