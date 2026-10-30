#!/usr/bin/env bash
# tags: conformance libc
# requires: python3
# timeout: 7200
# musl's libc-test (LC-19) in portable mode: the functional, regression, math and musl-internal tests, plus the
# api headers check, each built against the portable sysroot with llvmnet and statically against native musl 1.2.5
# (scripts/musl-native-cc.sh). A test passes when it exits with 0; a test counts when it passes natively, so the
# pass rate measures parity with native musl. Tests that need dynamic loading (dlopen, DSOs) are not built.
#   tests/libc-conformance.sh [functional] [regression] [math] [musl] [api]    default: all
#   LIBC_CONFORMANCE_JOBS=16                                                    parallel tests (default: nproc)
# Results: artifacts/tests/libc-conformance/{summary.tsv,results.tsv,failures.txt}; fails below LIBC_THRESHOLD
# percent (default 0: report only).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
suite="$("$root/scripts/fetch-sources.sh" --path libc-test)"
rm -rf "$test_output"
mkdir -p "$test_output/native/include" "$test_output/llvmnet/include"
groups=("$@")
[[ ${#groups[@]} -gt 0 ]] || groups=(functional regression math musl api)
flags=(-std=c99 -D_POSIX_C_SOURCE=200809L -D_FILE_OFFSET_BITS=64 -fno-builtin -frounding-math -w -O1)
# options.h records the POSIX option groups of each C library, as libc-test's Makefile generates it.
options() {
    "$@" "${flags[@]}" -E -x c "$suite/src/common/options.h.in" |
        sed -e '1,/optiongroups_unistd_end/d' -e '/^#/d' -e '/^[[:space:]]*$/d' -e 's/^/#define /'
}
bash "$root/scripts/musl-native-cc.sh" --version >/dev/null 2>&1 || true
stage="${LLVMNET_VENDOR_DIR:-$root/artifacts/vendor}/musl-native/root"
options clang-22 -nostdinc -isystem "$stage/usr/include/x86_64-linux-musl" -isystem "$(clang-22 -print-resource-dir)/include" \
    >"$test_output/native/include/options.h"
options "$llvmnet" --runtime=portable >"$test_output/llvmnet/include/options.h"
python3 - "$suite" "$test_output" "$llvmnet" "$root/scripts/musl-native-cc.sh" "$stage" "${LIBC_THRESHOLD:-0}" "${groups[*]}" "${flags[@]}" <<'PYEOF'
import concurrent.futures, glob, os, shutil, subprocess, sys

suite, output, llvmnet, native_cc, stage, threshold, groups, flags = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], float(sys.argv[6]), sys.argv[7].split(), sys.argv[8:]
resource = subprocess.run(['clang-22', '-print-resource-dir'], capture_output=True, text=True).stdout.strip()
jobs = int(os.environ.get('LIBC_CONFORMANCE_JOBS', os.cpu_count()))
common = sorted(path for path in glob.glob(f'{suite}/src/common/*.c') if not path.endswith('runtest.c'))
# Dynamic loading is not available; tls_align's static build links its companion object, as libc-test does.
skipped = {'functional/dlopen', 'functional/tls_align_dlopen', 'functional/tls_init_dlopen', 'regression/tls_get_new-dtv'}
companions = {'functional/tls_align': ['functional/tls_align_dso.c']}

def run(command, cwd, timeout):
    try:
        result = subprocess.run(command, cwd=cwd, timeout=timeout, capture_output=True, stdin=subprocess.DEVNULL)
        return result.returncode, result.stdout.decode(errors='replace')
    except subprocess.TimeoutExpired:
        return 'timeout', ''

def build(kind, directory, sources, compile_only):
    os.makedirs(directory, exist_ok=True)
    include = ['-I', f'{output}/{kind}/include', '-I', f'{suite}/src/common', '-I', f'{suite}/src/math']
    if kind == 'native':
        command = ['bash', native_cc] if not compile_only else \
            ['clang-22', '-c', '-nostdinc', '-isystem', f'{stage}/usr/include/x86_64-linux-musl', '-isystem', f'{resource}/include']
    else:
        command = [llvmnet, '--runtime=portable'] + (['-c'] if compile_only else [])
    target = 'test.o' if compile_only else 'test'
    status, log = run(command + flags + include + sources + ([] if compile_only else common) + ['-o', target], directory, 600)
    if status == 0: return None
    return 'compile timeout' if status == 'timeout' else 'rejected' if kind == 'llvmnet' and status == 1 else 'compile'

def check(test):
    group, name = test
    sources = [f'{suite}/src/{name}.c'] + [f'{suite}/src/{c}' for c in companions.get(name, [])]
    work = os.path.join(output, 'work', name.replace('/', '_'))
    compile_only = group == 'api'
    results = {}
    for kind in ('native', 'llvmnet'):
        error = build(kind, os.path.join(work, kind), sources, compile_only)
        if error: results[kind] = (error, '')
        elif compile_only: results[kind] = (0, '')
        else: results[kind] = run(['./test'], os.path.join(work, kind), 120)
        if kind == 'native' and results[kind][0] != 0:
            shutil.rmtree(work, ignore_errors=True)
            return group, name, 'native-fails', str(results[kind][0])
    status, text = results['llvmnet']
    if status == 0:
        shutil.rmtree(work, ignore_errors=True)
        return group, name, 'pass', ''
    first = next((line for line in text.splitlines() if line.strip()), '')[:160]
    return group, name, 'fail', (f'exit {status}' if isinstance(status, int) else status) + (f': {first}' if first else '')

tests = []
for group in groups:
    for path in sorted(glob.glob(f'{suite}/src/{group}/*.c')):
        name = os.path.relpath(path, f'{suite}/src')[:-2]
        if name.endswith('_dso') or name in skipped or name == 'api/main': continue
        tests.append((group, name))
with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
    results = list(pool.map(check, tests))
with open(f'{output}/results.tsv', 'w') as table:
    for row in results: table.write('\t'.join(row) + '\n')
open(f'{output}/failures.txt', 'w').write('\n'.join(f'{g}\t{n}\t{r}' for g, n, s, r in sorted(results) if s == 'fail') + '\n')
low = []
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('group\tcounted\tpassed\tfailed\tnative failures (not counted)\tpass rate\n')
    for group in groups:
        rows = [r for r in results if r[0] == group]
        passed = sum(r[2] == 'pass' for r in rows); failed = sum(r[2] == 'fail' for r in rows)
        native = sum(r[2] == 'native-fails' for r in rows)
        rate = 100.0 * passed / (passed + failed) if passed + failed else 100.0
        summary.write(f'{group}\t{passed + failed}\t{passed}\t{failed}\t{native}\t{rate:.1f}\n')
        print(f'{group}: {passed} of {passed + failed} pass in portable mode ({rate:.1f}%); {native} fail natively and are not counted')
        if rate < threshold: low.append(group)
for g, n, s, r in sorted(results):
    if s == 'fail': print(f'  FAIL {n}: {r}')
if low:
    print(f'FAIL: below {threshold}%: {" ".join(low)}', file=sys.stderr)
    sys.exit(1)
PYEOF
printf 'libc-test results: %s\n' "$test_output/summary.tsv"
