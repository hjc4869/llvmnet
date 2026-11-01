#!/usr/bin/env bash
# tags: optional aot cpp
# requires: clang++ system-cxx
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
archive="$(realpath "${1:-${LLVMNET_SYSTEM_CXX:-$root/artifacts/system-cxx/libstdcxx-system.a}}")"
output="$root/artifacts/tests/system-cxx"
mkdir -p "$output"
clang++-22 -std=c++14 -pthread -O3 "$root/tests/cpp/streams.cpp" -o "$output/native"
timeout 30 "$output/native" "$output/native-file.txt" > "$output/native.txt"
for optimization in 0 3; do
    target="$output/streams-O$optimization"
    "$root/bin/llvmnet" --runtime=system -std=c++14 -O"$optimization" "$root/tests/cpp/streams.cpp" "$archive" -o "$target.dll"
    timeout 30 dotnet "$target.dll" "$target-file.txt" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    diff -u "$output/native-file.txt" "$target-file.txt"
    "$root/bin/llvmnet" --runtime=system --nativeaot -std=c++14 -O"$optimization" "$root/tests/cpp/streams.cpp" "$archive" -o "$target-aot"
    timeout 30 "$target-aot" "$target-aot-file.txt" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    diff -u "$output/native-file.txt" "$target-aot-file.txt"
    printf 'PASS: upstream libstdc++ O%s streams, locale, hash tables and four synchronized C++ workers in JIT/NativeAOT\n' "$optimization"
done
fixture="$(mktemp -d "$output/filesystem.XXXXXXXX")"
clang++-22 -std=c++17 -O1 "$root/tests/cpp/filesystem.cpp" -o "$output/filesystem-native"
"$output/filesystem-native" "$fixture/native" > "$output/filesystem-native.txt"
"$root/bin/llvmnet" --runtime=system -std=c++17 -O1 "$root/tests/cpp/filesystem.cpp" "$archive" -o "$output/filesystem.dll"
dotnet "$output/filesystem.dll" "$fixture/jit" > "$output/filesystem-jit.txt"
diff -u "$output/filesystem-native.txt" "$output/filesystem-jit.txt"
"$root/bin/llvmnet" --runtime=system --nativeaot -std=c++17 -O1 "$root/tests/cpp/filesystem.cpp" "$archive" -o "$output/filesystem-aot"
"$output/filesystem-aot" "$fixture/aot" > "$output/filesystem-aot.txt"
diff -u "$output/filesystem-native.txt" "$output/filesystem-aot.txt"
printf 'PASS: upstream C++17 filesystem status, iteration and operations in JIT/NativeAOT\n'
clang++-22 -std=c++14 -pthread -O3 "$root/tests/cpp/futures.cpp" -o "$output/futures-native"
timeout 30 "$output/futures-native" > "$output/futures-native.txt"
for optimization in 0 3; do
    target="$output/futures-O$optimization"
    "$root/bin/llvmnet" --runtime=system -std=c++14 -O"$optimization" "$root/tests/cpp/futures.cpp" "$archive" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/futures-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime=system --nativeaot -std=c++14 -O"$optimization" "$root/tests/cpp/futures.cpp" "$archive" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/futures-native.txt" "$target-aot.txt"
    printf 'PASS: upstream C++ futures O%s promises, waits, errors and thread-exit results in JIT/NativeAOT\n' "$optimization"
done
for standard in c++20 c++23; do
    clang++-22 -std="$standard" -pthread -O2 "$root/tests/cpp/cxx20.cpp" -o "$output/$standard-native"
    timeout 30 "$output/$standard-native" > "$output/$standard-native.txt"
    for optimization in 0 2; do
        target="$output/$standard-O$optimization"
        "$root/bin/llvmnet" --runtime=system -std="$standard" -O"$optimization" "$root/tests/cpp/cxx20.cpp" "$archive" -o "$target.dll"
        timeout 30 dotnet "$target.dll" > "$target.txt"
        diff -u "$output/$standard-native.txt" "$target.txt"
    done
done
"$root/bin/llvmnet" --runtime=system --nativeaot -std=c++20 -O2 "$root/tests/cpp/cxx20.cpp" "$archive" -o "$output/c++20-aot"
timeout 30 "$output/c++20-aot" > "$output/c++20-aot.txt"
diff -u "$output/c++20-native.txt" "$output/c++20-aot.txt"
printf 'PASS: upstream C++20 stringstream::str(), format, atomic waits, osyncstream and time zones, and C++23 println, in JIT/NativeAOT\n'
clang++-22 -std=c++14 -O3 "$root/tests/cpp/random-device.cpp" -o "$output/random-device-native"
timeout 30 "$output/random-device-native" > "$output/random-device-native.txt"
for optimization in 0 3; do
    target="$output/random-device-O$optimization"
    "$root/bin/llvmnet" --runtime=system -std=c++14 -O"$optimization" "$root/tests/cpp/random-device.cpp" "$archive" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/random-device-native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime=system --nativeaot -std=c++14 -O"$optimization" "$root/tests/cpp/random-device.cpp" "$archive" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/random-device-native.txt" "$target-aot.txt"
    printf 'PASS: upstream C++ random_device O%s OS entropy and seeded engines in JIT/NativeAOT\n' "$optimization"
done