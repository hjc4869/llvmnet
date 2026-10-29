#!/usr/bin/env bash
# tags: core libc aot
# timeout: 2400
# musl 1.2.5 on dotnet64-v1 (LC-01, LC-02 spike): build the port, then compare stdio, libm, system and thread
# probes with the same programs statically linked against native musl 1.2.5, under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
libc="$test_output/libc"
bash "$root/scripts/build-portable-libc.sh" "$libc" >"$test_output/build.log"
tail -n 1 "$test_output/build.log"
if [[ -s "$libc/build/failures.tsv" ]]; then
    printf 'FAIL: musl sources failed to compile:\n' >&2
    cat "$libc/build/failures.tsv" >&2
    exit 1
fi
for probe in stdio math system threads; do
    source_file="$root/tests/c/musl/$probe.c"
    (cd "$test_output" && "$root/scripts/musl-native-cc.sh" -O1 "$source_file" -o "$probe-native" 2>/dev/null && "./$probe-native" >"$probe-native.txt")
    "$llvmnet" --runtime=portable --portable-libc "$libc" -O1 "$source_file" -o "$test_output/musl-$probe.dll"
    (cd "$test_output" && timeout 120 dotnet "musl-$probe.dll" >"$probe.txt")
    diff -u "$test_output/$probe-native.txt" "$test_output/$probe.txt"
    "$llvmnet" --runtime=portable --portable-libc "$libc" -O1 --nativeaot "$source_file" -o "$test_output/musl-$probe-aot" >"$test_output/aot-$probe.log"
    (cd "$test_output" && timeout 120 "./musl-$probe-aot" >"$probe-aot.txt")
    diff -u "$test_output/$probe-native.txt" "$test_output/$probe-aot.txt"
    # Nothing but the host interface may resolve outside musl: the old hand-written C# C library is unused.
    "$llvmnet" --runtime=portable --portable-libc "$libc" -O1 "$source_file" --emit-llvm -o "$test_output/$probe-linked.bc"
    unexpected="$(llvm-nm-22 -u "$test_output/$probe-linked.bc" | awk '{print $2}' |
        grep -v -E '^(llvm\..*|__llvmnet_(syscall|get_tp|clone|run_constructors)|setjmp|longjmp|_DYNAMIC|__(init|fini)_array_(start|end))$' || true)"
    if [[ -n "$unexpected" ]]; then
        printf 'FAIL: %s resolves symbols outside musl and the host interface: %s\n' "$probe" "$unexpected" >&2
        exit 1
    fi
done
printf 'PASS: musl stdio, libm, system and thread probes match native musl under JIT and NativeAOT\n'
