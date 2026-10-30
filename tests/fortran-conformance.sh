#!/usr/bin/env bash
# tags: conformance fortran
# requires: flang python3
# timeout: 14400
# Fortran conformance in portable mode: the NIST FCVS F77 suite and the run tests of the gfortran
# test-suite port in llvm-test-suite. Each program is built natively with flang-22 and with llvmnet; a
# test counts when the native build passes it (FCVS: output equals the reference; gfortran: exit status 0, or
# the expected failure for xfail tests), and llvmnet passes when its build behaves the same.
#   tests/fortran-conformance.sh [fcvs] [gfortran]        default: both
#   FORTRAN_CONFORMANCE_JOBS=16                            parallel tests (default: 8; each runs .NET processes, so on a
#                                                          shared machine run inside a memory cap, see libc-conformance.sh)
#   FORTRAN_CONFORMANCE_FILTER=regression/pr1              only gfortran tests whose path contains this text
# Results: artifacts/tests/fortran-conformance/{summary.tsv,results.tsv,failures.txt}. Fails if a suite's pass
# rate is below FORTRAN_THRESHOLD percent (default 0: report only).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
suite="$("$root/scripts/fetch-sources.sh" --path llvm-test-suite)/Fortran"
rm -rf "$test_output"
mkdir -p "$test_output"
suites=("$@")
[[ ${#suites[@]} -gt 0 ]] || suites=(fcvs gfortran)
python3 - "$suite" "$test_output" "$llvmnet" "${FORTRAN_THRESHOLD:-0}" "${suites[@]}" <<'PYEOF'
import concurrent.futures, os, re, shutil, subprocess, sys

suite, output, llvmnet, threshold, selected = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]), sys.argv[5:]
jobs = int(os.environ.get('FORTRAN_CONFORMANCE_JOBS', 8))
name_filter = os.environ.get('FORTRAN_CONFORMANCE_FILTER', '')
fortran = re.compile(r'\.(f|f90|f95|f03|f08|F|F90|F95|F03|F08|for)$')

def run(command, cwd, timeout, stdin=None):
    try:
        result = subprocess.run(command, cwd=cwd, timeout=timeout, capture_output=True, stdin=stdin or subprocess.DEVNULL)
        return result.returncode, result.stdout
    except subprocess.TimeoutExpired:
        return 'timeout', b''

def build(kind, directory, sources, options):
    """Builds a program natively (flang-22, C parts with clang-22) or with llvmnet; returns None or an error tag."""
    os.makedirs(directory, exist_ok=True)
    if kind == 'native':
        objects = []
        for source in sources:
            if source.endswith('.c'):
                obj = os.path.join(directory, os.path.basename(source) + '.o')
                if run(['clang-22', '-c', source, '-o', obj], directory, 120)[0] != 0: return 'compile'
                objects.append(obj)
        command = ['flang-22', '-w', *options, '-module-dir', directory, *[s for s in sources if not s.endswith('.c')], *objects, '-o', 'program']
    else:
        command = [llvmnet, '--runtime=portable', '-w', *options, '-module-dir', directory, *sources, '-o', 'program']
    try:
        result = subprocess.run(command, cwd=directory, timeout=600, capture_output=True, stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        return 'compile timeout'
    if result.returncode == 0: return None
    if kind == 'llvmnet' and result.returncode == 1:
        first = next((l for l in result.stderr.decode(errors='replace').splitlines() if 'error' in l), '')
        return 'rejected: ' + first.replace('llvmnet: error: ', '')[:140]
    return 'compile'

def outcome(kind, directory, sources, options, timeout):
    error = build(kind, directory, sources, options)
    if error: return error, b''
    status, stdout = run(['./program'], directory, timeout)
    return ('timeout' if status == 'timeout' else status), stdout

def fcvs_tests():
    root = os.path.join(suite, 'UnitTests', 'fcvs21_f95')
    for name in sorted(os.listdir(root)):
        # FM509 needs -std=legacy, which flang does not accept; the test-suite skips it the same way.
        if re.fullmatch(r'FM\d{3}\.f', name) and name != 'FM509.f':
            yield 'fcvs', name[:-2], [os.path.join(root, name)], [], False, os.path.join(root, name[:-2] + '.reference_output')

def gfortran_tests():
    root = os.path.join(suite, 'gfortran')
    for directory, _, files in sorted(os.walk(root)):
        if 'tests.cmake' not in files: continue
        relative = os.path.relpath(directory, root)
        # OpenMP, OpenACC, coarrays, sanitizers, LTO and debugger tests need features llvmnet does not provide.
        if re.search(r'(^|/)(gomp|goacc|goacc-gomp|coarray|asan|ubsan|lto|guality|prof|debug|analyzer)(/|$)', relative): continue
        disabled = set()
        for cmake in files:
            if cmake.startswith('DisabledFiles'):
                disabled |= set(re.findall(r'[\w+.-]+\.(?:f|f90|f95|f03|f08|F|F90|F95|F03|F08)\b', open(os.path.join(directory, cmake)).read()))
        for line in open(os.path.join(directory, 'tests.cmake')):
            if line.startswith('#') or not line.strip(): continue
            kind, sources, xfail, options, enabled, disabled_on = (line.rstrip('\n').split(';') + [''] * 6)[:6]
            sources = sources.split()
            if kind != 'run' or not sources or sources[0] in disabled: continue
            if enabled.strip() and not any(re.search(p, 'x86_64-unknown-linux-gnu') for p in enabled.split()): continue
            if any(re.search(p, 'x86_64-unknown-linux-gnu') for p in disabled_on.split()): continue
            test = f'{relative}/{sources[0]}'
            if name_filter and name_filter not in test: continue
            yield 'gfortran', test, [os.path.join(directory, s) for s in sources], options.split(), xfail == 'xfail', None

def with_status(result):
    """The test-suite's reference outputs end with the exit status its runner appends after the program's output."""
    status, stdout = result
    if not isinstance(status, int): return None
    if stdout and not stdout.endswith(b'\n'): stdout += b'\n'
    return stdout + f'exit {status}\n'.encode()

def check(test):
    group, name, sources, options, xfail, reference = test
    work = os.path.join(output, 'work', group, name.replace('/', '_'))
    native = outcome('native', os.path.join(work, 'native'), sources, options, 60)
    if reference is not None:
        expected = open(reference, 'rb').read()
        native_ok = with_status(native) == expected
    else:
        native_ok = (native[0] != 0) if xfail else native[0] == 0
    if not native_ok:
        shutil.rmtree(work, ignore_errors=True)
        return group, name, 'native-fails', ''
    managed = outcome('llvmnet', os.path.join(work, 'llvmnet'), sources, options, 120)
    if reference is not None:
        passed = with_status(managed) == expected
        reason = 'output differs' if isinstance(managed[0], int) else str(managed[0])
    elif xfail:
        passed, reason = managed[0] != 0, 'expected failure did not happen'
    else:
        passed = managed[0] == 0
        reason = managed[0] if isinstance(managed[0], str) else f'exit {managed[0]}'
    if passed: shutil.rmtree(work, ignore_errors=True)
    return group, name, 'pass' if passed else 'fail', '' if passed else str(reason)

tests = [t for g in selected for t in (fcvs_tests() if g == 'fcvs' else gfortran_tests())]
results = []
with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
    for result in pool.map(check, tests):
        results.append(result)
with open(os.path.join(output, 'results.tsv'), 'w') as table:
    for row in results: table.write('\t'.join(row) + '\n')
failures = sorted(f'{g}\t{n}\t{r}' for g, n, s, r in results if s == 'fail')
open(os.path.join(output, 'failures.txt'), 'w').write('\n'.join(failures) + '\n')
low = []
with open(os.path.join(output, 'summary.tsv'), 'w') as summary:
    summary.write('suite\tcounted\tpassed\tfailed\tnative failures (not counted)\tpass rate\n')
    for group in selected:
        rows = [r for r in results if r[0] == group]
        passed = sum(r[2] == 'pass' for r in rows); failed = sum(r[2] == 'fail' for r in rows)
        native = sum(r[2] == 'native-fails' for r in rows)
        rate = 100.0 * passed / (passed + failed) if passed + failed else 100.0
        summary.write(f'{group}\t{passed + failed}\t{passed}\t{failed}\t{native}\t{rate:.1f}\n')
        print(f'{group}: {passed} of {passed + failed} pass in portable mode ({rate:.1f}%); {native} tests fail natively and are not counted')
        if rate < threshold: low.append(group)
reasons = {}
for _, _, status, reason in results:
    if status == 'fail': reasons[reason] = reasons.get(reason, 0) + 1
for reason, count in sorted(reasons.items(), key=lambda item: -item[1]): print(f'  {count:5d} {reason}')
if low:
    print(f'FAIL: below {threshold}%: {" ".join(low)}', file=sys.stderr)
    sys.exit(1)
PYEOF
printf 'Fortran conformance results: %s\n' "$test_output/summary.tsv"
