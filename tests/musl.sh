#!/usr/bin/env bash
# tags: core libc aot
# timeout: 2400
# musl in the portable sysroot: stdio, libm, system and thread probes compared with the same programs statically
# linked against native musl 1.2.5, under JIT and NativeAOT.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
for probe in stdio math system threads; do
    source_file="$root/tests/c/musl/$probe.c"
    (cd "$test_output" && "$root/scripts/musl-native-cc.sh" -O1 "$source_file" -o "$probe-native" 2>/dev/null && "./$probe-native" >"$probe-native.txt")
    "$llvmnet" --runtime=portable -O1 "$source_file" -o "$test_output/musl-$probe.dll"
    (cd "$test_output" && timeout 120 dotnet "musl-$probe.dll" >"$probe.txt")
    diff -u "$test_output/$probe-native.txt" "$test_output/$probe.txt"
    "$llvmnet" --runtime=portable -O1 --nativeaot "$source_file" -o "$test_output/musl-$probe-aot" >"$test_output/aot-$probe.log"
    (cd "$test_output" && timeout 120 "./musl-$probe-aot" >"$probe-aot.txt")
    diff -u "$test_output/$probe-native.txt" "$test_output/$probe-aot.txt"
    # Nothing but the host interface may resolve outside musl.
    "$llvmnet" --runtime=portable -O1 "$source_file" --emit-llvm -o "$test_output/$probe-linked.bc"
    unexpected="$(llvm-nm-22 -u "$test_output/$probe-linked.bc" | awk '{print $2}' |
        grep -v -E '^(llvm\..*|__llvmnet_(syscall|get_tp|clone|spawn|run_constructors)|setjmp|longjmp|_DYNAMIC|__(init|fini)_array_(start|end))$' || true)"
    if [[ -n "$unexpected" ]]; then
        printf 'FAIL: %s resolves symbols outside musl and the host interface: %s\n' "$probe" "$unexpected" >&2
        exit 1
    fi
done
printf 'PASS: musl stdio, libm, system and thread probes match native musl under JIT and NativeAOT\n'
