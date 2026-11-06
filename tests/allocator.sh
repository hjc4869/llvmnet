#!/usr/bin/env bash
# tags: core libc cpp aot
# requires: clang++
# timeout: 1800
# The portable C library's allocator, mimalloc (ports/mimalloc), under JIT and NativeAOT: the malloc family's results
# and errno against native musl (tests/c/malloc-api.c); 4,000 short-lived threads, producers and consumers that free
# each other's blocks, thread-exit destructors that allocate, and detached threads still allocating at exit, against
# native musl (tests/c/malloc-threads.c); a program that replaces the allocator and defines names of mimalloc's own
# (tests/c/malloc-replace.c); C++'s operator new and delete on threads against a native clang++ build
# (tests/cpp/malloc-new.cpp), which also checks that what operator new returns is a block of the C library's malloc.
# MIMALLOC_VERBOSE shows that the C programs run on mimalloc, and its object in the sysroot that it takes memory from
# the host, not from mmap.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
run_all() {
    local name="$1" native="$2"
    shift 2
    (cd "$test_output" && "$native" >"$name-native.txt")
    "$llvmnet" --runtime=portable "$@" -o "$test_output/$name.dll"
    (cd "$test_output" && timeout 600 dotnet "$name.dll" >"$name.txt")
    diff -u "$test_output/$name-native.txt" "$test_output/$name.txt"
    "$llvmnet" --runtime=portable --nativeaot "$@" -o "$test_output/$name-aot" >"$test_output/$name-aot.log"
    (cd "$test_output" && timeout 600 "./$name-aot" >"$name-aot.txt")
    diff -u "$test_output/$name-native.txt" "$test_output/$name-aot.txt"
}
for program in malloc-api malloc-threads malloc-replace; do
    source_file="$root/tests/c/$program.c"
    "$root/scripts/musl-native-cc.sh" -O1 -fno-builtin -w "$source_file" -o "$test_output/$program-native"
    run_all "$program" "./$program-native" -O1 -fno-builtin -w "$source_file"
done
# The C programs run on mimalloc, which takes memory from the host's allocator entry points only: its object in libc.a
# calls neither the memory system calls nor musl's wrappers of them.
MIMALLOC_VERBOSE=1 dotnet "$test_output/malloc-api.dll" 2>"$test_output/verbose.txt" >/dev/null
grep -q '^mimalloc: process init' "$test_output/verbose.txt"
(cd "$test_output" && llvm-ar-22 x "$root/sysroot/dotnet64-v2/lib/libc.a" mimalloc.o)
llvm-nm-22 --undefined-only "$test_output/mimalloc.o" >"$test_output/mimalloc-undefined.txt"
grep -q ' __llvmnet_memory_allocate$' "$test_output/mimalloc-undefined.txt"
if grep -E ' (__)?(mmap|munmap|mremap|madvise|posix_madvise|mprotect|brk|sbrk|syscall)$' "$test_output/mimalloc-undefined.txt"; then
    printf 'FAIL: mimalloc calls the memory system calls\n' >&2
    exit 1
fi
clang++-22 -std=c++17 -O1 -pthread "$root/tests/cpp/malloc-new.cpp" -o "$test_output/malloc-new-native"
run_all malloc-new ./malloc-new-native -std=c++17 -O1 "$root/tests/cpp/malloc-new.cpp"
printf 'PASS: the malloc family, 4,000-thread allocation, a replaced allocator and C++ new/delete on mimalloc match native under JIT and NativeAOT\n'
