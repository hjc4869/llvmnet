#!/usr/bin/env bash
# tags: core libc cpp aot
# requires: clang++
# timeout: 2400
# Upstream libc++/libc++abi 22.1.8 on the musl port (CX-01, CX-07): a broad smoke program under JIT and
# NativeAOT plus the existing C++ fixtures whose output does not depend on glibc/libstdc++ specifics, compared
# with native clang++. Not covered here: random-device (libstdc++ "default" token), terminate (exits on purpose;
# see cpp-terminate.sh), thread-local and exit order (thread-local.sh, exit-order.sh run them in portable mode).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
compare() {
    # Optional fourth argument: "file" or "directory" gives each side its own scratch path as argv[1].
    local name="$1" source_file="$2" standard="$3" kind="${4:-}" native_argument=() managed_argument=()
    if [[ "$kind" == file ]]; then native_argument=("$name-native.data"); managed_argument=("$name-llvmnet.data"); fi
    # The filesystem fixture creates its directory, so the path must not exist yet.
    if [[ "$kind" == directory ]]; then native_argument=("$name-native.dir"); managed_argument=("$name-llvmnet.dir"); fi
    clang++-22 -std="$standard" -O1 -pthread "$source_file" -o "$test_output/$name-native" 2>/dev/null
    (cd "$test_output" && "./$name-native" "${native_argument[@]}" >"$name-native.txt")
    "$llvmnet" --runtime=portable -std="$standard" -O1 "$source_file" -o "$test_output/$name.dll" 2>"$test_output/$name.log"
    (cd "$test_output" && timeout 120 dotnet "$name.dll" "${managed_argument[@]}" >"$name.txt")
    diff -u "$test_output/$name-native.txt" "$test_output/$name.txt"
}
compare smoke "$root/tests/cpp/libcxx/smoke.cpp" c++23
"$llvmnet" --runtime=portable -std=c++23 -O1 --nativeaot "$root/tests/cpp/libcxx/smoke.cpp" -o "$test_output/smoke-aot" >"$test_output/aot.log" 2>&1
(cd "$test_output" && timeout 120 ./smoke-aot >smoke-aot.txt)
diff -u "$test_output/smoke-native.txt" "$test_output/smoke-aot.txt"
for fixture in core rtti exception-filters exception-pointer aggregate-callbacks futures nothrow portable portable-spec-clock allocation-abi; do
    compare "$fixture" "$root/tests/cpp/$fixture.cpp" c++17
done
compare streams "$root/tests/cpp/streams.cpp" c++17 file
compare filesystem "$root/tests/cpp/filesystem.cpp" c++17 directory
printf 'PASS: libc++ and libc++abi on musl: smoke program (JIT and NativeAOT) and 12 C++ fixtures match native\n'
