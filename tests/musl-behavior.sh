#!/usr/bin/env bash
# tags: core libc aot
# timeout: 2400
# The portable C library against the same programs linked with native musl 1.2.5, under JIT and NativeAOT: a
# corpus of printf, scanf and strtod conversions and a thread lifecycle stress test. musl is the same C code in
# both builds, so any difference is a translation or host-layer bug.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
for program in format-corpus thread-stress; do
    source_file="$root/tests/c/$program.c"
    "$root/scripts/musl-native-cc.sh" -O1 -w "$source_file" -o "$test_output/$program-native" 2>/dev/null
    (cd "$test_output" && "./$program-native" >"$program-native.txt")
    "$llvmnet" --runtime=portable -O1 -w "$source_file" -o "$test_output/$program.dll"
    (cd "$test_output" && timeout 600 dotnet "$program.dll" >"$program.txt")
    diff -u "$test_output/$program-native.txt" "$test_output/$program.txt"
    "$llvmnet" --runtime=portable -O1 -w --nativeaot "$source_file" -o "$test_output/$program-aot" >"$test_output/$program-aot.log"
    (cd "$test_output" && timeout 600 "./$program-aot" >"$program-aot.txt")
    diff -u "$test_output/$program-native.txt" "$test_output/$program-aot.txt"
done
printf 'PASS: %d printf/scanf/strtod results and 10,000-thread lifecycle match native musl under JIT and NativeAOT\n' \
    "$(wc -l <"$test_output/format-corpus-native.txt")"
