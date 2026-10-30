#!/usr/bin/env bash
# tags: core libc
# requires: python3
# timeout: 1200
# libm accuracy (LC-21): tests/c/libm-random.c evaluates 89 float, double and long double functions on 2,000
# deterministic random inputs each. The portable build (musl's C libm translated to CIL) must equal native musl bit
# for bit, except where native musl uses x86-64 assembly instead of the C code (the x87 long double functions and
# sqrt) or the C code calls an inexact one (coshl calls expl, for example); those, and the host libm (glibc), are
# reported as maximum ULP differences.
# Results: artifacts/tests/libm-accuracy/summary.tsv.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source_file="$root/tests/c/libm-random.c"
clang-22 -O1 -fno-builtin -w "$source_file" -lm -o "$test_output/host"
"$test_output/host" >"$test_output/host.txt"
"$root/scripts/musl-native-cc.sh" -O1 -fno-builtin -w "$source_file" -o "$test_output/musl" 2>/dev/null
"$test_output/musl" >"$test_output/musl.txt"
"$llvmnet" --runtime=portable -O1 -fno-builtin -w "$source_file" -o "$test_output/libm-random.dll"
dotnet "$test_output/libm-random.dll" >"$test_output/portable.txt"
python3 - "$test_output" "$root/artifacts/vendor/src/musl-1.2.5/src/math" <<'PYEOF'
import os, re, sys

output, math = sys.argv[1], sys.argv[2]
assembly = {os.path.splitext(name)[0] for name in os.listdir(f'{math}/x86_64')}
# Assembly for exact or correctly rounded operations gives the same bits as the C code, so its callers stay checked.
exact = {'fabs', 'fabsf', 'fabsl', 'ceill', 'floorl', 'truncl', 'rintl', 'sqrt', 'sqrtf', 'sqrtl', 'fma', 'fmaf',
         'lrint', 'lrintf', 'lrintl', 'llrint', 'llrintf', 'llrintl', 'remainderl', 'remquol', 'fmodl'}
inexact = re.compile(r'\b(' + '|'.join(sorted(assembly - exact)) + r')\s*\(')

def native_assembly(name):
    """'yes' when native musl implements the function in assembly, the inexact assembly functions its C code calls, or ''."""
    if name in assembly: return 'yes'
    path = f'{math}/{name}.c'
    if not os.path.exists(path):
        path = next(f'{math}/{file}' for file in sorted(os.listdir(math)) if file.endswith('.c') and
                    re.search(rf'^\w[\w ]*\b{name}\(', open(f'{math}/{file}').read(), re.M))
    return ' '.join(sorted(set(inexact.findall(open(path).read())) - {name}))

def results(path):
    table, name = {}, None
    for line in open(path):
        if line.startswith('#'):
            _, name, kind, _ = line.split()
            table[name] = (kind, [])
        else:
            table[name][1].append(int(line, 16))
    return table

def ordered(kind, bits):
    """Maps a result to an integer whose difference counts ULPs; None for NaN."""
    if kind == 'l':
        sign, exponent, mantissa = bits >> 79, bits >> 64 & 0x7fff, bits & (1 << 64) - 1
        if exponent == 0x7fff and mantissa & (1 << 63) - 1: return None
        magnitude = exponent << 63 | mantissa & (1 << 63) - 1
    else:
        width = 32 if kind == 'f' else 64
        sign, magnitude = bits >> width - 1, bits & (1 << width - 1) - 1
        if magnitude > (0xff << 23 if kind == 'f' else 0x7ff << 52): return None
    return -magnitude if sign else magnitude

def compare(kind, expected, actual):
    """Maximum ULP difference and the number of differing results; a NaN against a number counts as infinite."""
    worst, differing = 0, 0
    for left, right in zip(expected, actual):
        if left == right: continue
        a, b = ordered(kind, left), ordered(kind, right)
        if a is None and b is None: continue
        differing += 1
        worst = float('inf') if a is None or b is None else max(worst, abs(a - b))
    return worst, differing

host, musl, portable = (results(f'{output}/{name}.txt') for name in ('host', 'musl', 'portable'))
failures = []
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('function\tnative musl uses assembly (or calls)\tdiffering from native musl\tmax ULP from native musl\t'
                  'differing from host libm\tmax ULP from host libm\n')
    for name, (kind, values) in portable.items():
        musl_worst, musl_differing = compare(kind, musl[name][1], values)
        host_worst, host_differing = compare(kind, host[name][1], values)
        uses = native_assembly(name)
        summary.write(f'{name}\t{uses or "no"}\t{musl_differing}\t{musl_worst}\t{host_differing}\t{host_worst}\n')
        if musl_differing and not uses: failures.append(name)
rows = [line.rstrip('\n').split('\t') for line in open(f'{output}/summary.tsv')][1:]
count = sum(len(values) for _, values in portable.values())
print(f'{len(rows)} functions, {count} results')
for row in rows:
    if row[2] != '0' or row[4] != '0':
        print(f'  {row[0]:9} native musl: {row[2]:>4} differ, max {row[3]:>6} ULP   host libm: {row[4]:>4} differ, max {row[5]} ULP'
              + ('' if row[1] == 'no' else '   (native musl: x87 assembly' + ('' if row[1] == 'yes' else f' in {row[1]}') + ')'))
if failures:
    print(f'FAIL: differs from the same musl C code built natively: {" ".join(failures)}', file=sys.stderr)
    sys.exit(1)
PYEOF
printf 'PASS: musl libm in portable mode equals native musl wherever both run the same C code; ULP report in %s\n' \
    "$test_output/summary.tsv"
