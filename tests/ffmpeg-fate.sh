#!/usr/bin/env bash
# tags: fate ffmpeg
# requires: python3 rsync
# serial: 90
# timeout: 14400
# WG-04: ffmpeg's FATE suite against the real ffmpeg and ffprobe programs. The pinned ffmpeg release is configured
# twice with the same asm-free, offline options and every component, natively with clang-22 and with llvmnet in
# portable mode, and `make fate` runs in both against the FATE samples, verified file by file against
# tests/ffmpeg/fate-suite.sha256. A test counts when the native build passes it; FATE compares each output with its
# own reference.
#   FFMPEG_FATE_SAMPLES=<dir>     the samples (default artifacts/vendor/fate-suite), fetched with rsync when missing
#   FFMPEG_FATE_SKIP_BUILD=1      reuse both builds
#   FFMPEG_FATE_JOBS=12           parallel tests and compile jobs (default: 12)
#   FFMPEG_FATE_THRESHOLD=99      fail below this pass rate (default 0: report only)
# Results: artifacts/tests/ffmpeg-fate/{summary.tsv,results.tsv,failures.txt}; the reports of failed tests stay in
# artifacts/ffmpeg-fate-portable/tests/data/fate/.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source_dir="${FFMPEG_SOURCE:-$("$root/scripts/fetch-sources.sh" --path ffmpeg)}"
samples="${FFMPEG_FATE_SAMPLES:-$root/artifacts/vendor/fate-suite}"
manifest="$root/tests/ffmpeg/fate-suite.sha256"
jobs="${FFMPEG_FATE_JOBS:-12}"
rm -rf "$test_output"
mkdir -p "$test_output" "$samples"
# The samples are fetched by hash: rsync brings missing or changed files, then every file the manifest names must
# match it. Files the manifest does not name are not used by any counted test until the manifest is updated.
if ! (cd "$samples" && sha256sum --quiet -c "$manifest" >/dev/null 2>&1); then
    rsync -a rsync://fate-suite.ffmpeg.org/fate-suite/ "$samples/" >"$test_output/rsync.log" 2>&1
fi
(cd "$samples" && sha256sum --quiet -c "$manifest") >"$test_output/samples.log" 2>&1 ||
    { cat "$test_output/samples.log" >&2; printf 'FAIL: the FATE samples do not match %s\n' "$manifest" >&2; exit 1; }
options=(--disable-asm --disable-inline-asm --disable-x86asm --disable-hwaccels --disable-autodetect
    --disable-runtime-cpudetect --disable-network --disable-doc --disable-debug --disable-stripping --enable-pthreads
    --samples="$samples")
# configure's check_func links "extern int f(); int main(void){ f(); }", a call without the arguments the definition
# takes. llvmnet compiles such a call to a run-time trap (TC-07), so the probe links as it does natively and both builds
# run configure unchanged, with the sources linked as src/.
build() {
    local name="$1" build_dir="$root/artifacts/ffmpeg-fate-$1"
    shift
    if [[ "${FFMPEG_FATE_SKIP_BUILD:-0}" != 1 ]]; then
        rm -rf "$build_dir"
        mkdir -p "$build_dir"
        ln -sfn "$source_dir" "$build_dir/src"
        (cd "$build_dir" && ./src/configure "${options[@]}" "$@") >"$test_output/configure-$name.log" 2>&1 ||
            { tail -20 "$test_output/configure-$name.log" >&2; return 1; }
        make -C "$build_dir" -j"$jobs" >"$test_output/build-$name.log" 2>&1 ||
            { tail -20 "$test_output/build-$name.log" >&2; return 1; }
    fi
    rm -rf "$build_dir/tests/data/fate"
    make -C "$build_dir" -k -j"$jobs" fate SAMPLES="$samples" >"$test_output/fate-$name.log" 2>&1 || true
}
build native --cc=clang-22 --cxx=clang++-22 --extra-cflags="-O1 -fno-vectorize -fno-slp-vectorize"
# The programs are launchers beside their managed images, run directly as FATE runs native programs.
build portable --cc="$llvmnet" --cxx="$root/bin/llvmnet-clang++" --host-cc=clang-22 --ar=llvm-ar-22 --ranlib=llvm-ranlib-22 \
    --nm=llvm-nm-22 --target-os=linux --arch=x86_64 --cpu=generic --enable-cross-compile \
    --extra-cflags="--runtime=portable -O1 -fno-vectorize -fno-slp-vectorize" --extra-ldflags="--runtime=portable"
python3 - "$root/artifacts/ffmpeg-fate-native" "$root/artifacts/ffmpeg-fate-portable" "$test_output" "${FFMPEG_FATE_THRESHOLD:-0}" <<'PYEOF'
import glob, os, sys

native_dir, portable_dir, output, threshold = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])
def reports(build):
    result = {}
    for path in glob.glob(f'{build}/tests/data/fate/*.rep'):
        fields = open(path, errors='replace').read().split(':')
        result[fields[0]] = fields[1].strip() if len(fields) > 1 else '?'
    return result
native, portable = reports(native_dir), reports(portable_dir)
rows = []
for test in sorted(native):
    if native[test] != '0': rows.append((test, 'native-fails', native[test]))
    elif test not in portable: rows.append((test, 'fail', 'not run'))
    elif portable[test] == '0': rows.append((test, 'pass', ''))
    else: rows.append((test, 'fail', f'status {portable[test]}'))
with open(f'{output}/results.tsv', 'w') as table:
    for row in rows: table.write('\t'.join(row) + '\n')
open(f'{output}/failures.txt', 'w').write(''.join(f'{t}\t{r}\n' for t, s, r in rows if s == 'fail'))
passed = sum(s == 'pass' for _, s, _ in rows); failed = sum(s == 'fail' for _, s, _ in rows)
skipped = sum(s == 'native-fails' for _, s, _ in rows)
rate = 100.0 * passed / (passed + failed) if passed + failed else 0.0
with open(f'{output}/summary.tsv', 'w') as summary:
    summary.write('tests run natively\tcounted\tpassed\tfailed\tnative failures (not counted)\tpass rate\n')
    summary.write(f'{len(native)}\t{passed + failed}\t{passed}\t{failed}\t{skipped}\t{rate:.1f}\n')
print(f'FATE in portable mode: {passed} of {passed + failed} pass ({rate:.1f}%); {skipped} fail natively and are not counted')
for test, status, reason in rows:
    if status == 'fail': print(f'  FAIL {test}: {reason}')
if not native or rate < threshold:
    print(f'FAIL: below {threshold}% or no native results', file=sys.stderr)
    sys.exit(1)
print('PASS: FATE')
PYEOF
