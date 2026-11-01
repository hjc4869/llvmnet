#!/usr/bin/env bash
# tags: conformance c
# requires: python3
# timeout: 3600
# WG-02: the single-exec suite of c-testsuite (pinned in scripts/sources.lock): 220 single-file C programs, each with
# the output it must print (stdout and stderr together) and exit status 0. Every test is built natively with clang-22
# against glibc and with llvmnet; a test counts when the native build passes it, and passes when the llvmnet build
# prints the expected output and exits with 0.
#   tests/c-testsuite.sh [portable] [system]          the modes to test (default: portable)
#   C_TESTSUITE_JOBS=8                                parallel tests (default: 8)
#   C_TESTSUITE_THRESHOLD=99                          fail below this pass rate in a mode (default 0: report only)
# Results: artifacts/tests/c-testsuite/{summary.tsv,results.tsv,failures.txt}.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
suite="$("$root/scripts/fetch-sources.sh" --path c-testsuite)"
rm -rf "$test_output"
mkdir -p "$test_output"
modes=("$@")
[[ ${#modes[@]} -gt 0 ]] || modes=(portable)
python3 - "$suite" "$test_output" "$llvmnet" "${C_TESTSUITE_THRESHOLD:-0}" "${modes[@]}" <<'PYEOF'
import concurrent.futures, glob, os, shutil, subprocess, sys

suite, output, llvmnet, threshold, modes = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]), sys.argv[5:]
jobs = int(os.environ.get('C_TESTSUITE_JOBS', 8))

def run(command, cwd, timeout):
    try:
        result = subprocess.run(command, cwd=cwd, timeout=timeout, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        return result.returncode, result.stdout
    except subprocess.TimeoutExpired:
        return 'timeout', b''

def check(path):
    name = os.path.basename(path)[:-2]
    expected = open(path + '.expected', 'rb').read()
    work = os.path.join(output, 'work', name)
    rows = []
    builds = [('native', ['clang-22', '-w', '-O1', path, '-o', 'test', '-lm'])]
    builds += [(mode, [llvmnet, f'--runtime={mode}', '-w', '-O1', path, '-o', 'test', '-lm']) for mode in modes]
    for kind, command in builds:
        directory = os.path.join(work, kind)
        os.makedirs(directory, exist_ok=True)
        status, log = run(command, directory, 600)
        if status != 0:
            result, reason = 'fail', 'compile timeout' if status == 'timeout' else 'compile: ' + next(
                (line for line in log.decode(errors='replace').splitlines() if 'error' in line), f'exit {status}')[:160]
        else:
            status, text = run(['./test'], directory, 120)
            if status == 0 and text == expected: result, reason = 'pass', ''
            elif status == 0: result, reason = 'fail', 'output differs'
            else: result, reason = 'fail', 'timeout' if status == 'timeout' else f'exit {status}'
        if kind == 'native':
            if result != 'pass':
                shutil.rmtree(work, ignore_errors=True)
                return [(mode, name, 'native-fails', reason) for mode in modes]
            continue
        rows.append((kind, name, result, reason))
    if all(row[2] == 'pass' for row in rows): shutil.rmtree(work, ignore_errors=True)
    return rows

tests = sorted(glob.glob(f'{suite}/tests/single-exec/*.c'))
with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
    results = [row for rows in pool.map(check, tests) for row in rows]
with open(f'{output}/results.tsv', 'w') as table:
    for row in results: table.write('\t'.join(row) + '\n')
open(f'{output}/failures.txt', 'w').write(''.join(f'{m}\t{n}\t{r}\n' for m, n, s, r in results if s == 'fail'))
low = []
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('mode\tcounted\tpassed\tfailed\tnative failures (not counted)\tpass rate\n')
    for mode in modes:
        rows = [r for r in results if r[0] == mode]
        passed = sum(r[2] == 'pass' for r in rows); failed = sum(r[2] == 'fail' for r in rows)
        native = sum(r[2] == 'native-fails' for r in rows)
        rate = 100.0 * passed / (passed + failed) if passed + failed else 100.0
        summary.write(f'{mode}\t{passed + failed}\t{passed}\t{failed}\t{native}\t{rate:.1f}\n')
        print(f'{mode}: {passed} of {passed + failed} pass ({rate:.1f}%); {native} fail natively and are not counted')
        if rate < threshold: low.append(mode)
for mode, name, status, reason in results:
    if status == 'fail': print(f'  FAIL {mode} {name}: {reason}')
if low:
    print(f'FAIL: below {threshold}%: {" ".join(low)}', file=sys.stderr)
    sys.exit(1)
print('PASS: c-testsuite single-exec')
PYEOF
