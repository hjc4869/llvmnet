#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/fortran-concurrent"
prefix="${LLVMNET_OPENMP_PREFIX:-$root/artifacts/system-openmp}"
library="${LLVMNET_OPENMP_LIBRARY:-$prefix/lib/libomp.so.5}"
mkdir -p "$output"
export LD_LIBRARY_PATH="$prefix/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export OMP_NUM_THREADS=4 OMP_THREAD_LIMIT=4 OMP_DYNAMIC=FALSE
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
clang-22 -O1 -I"$prefix/include" -c "$root/tests/c/concurrent-worker.c" -o "$output/worker-native.o"
flang-22 -O3 -fopenmp -fdo-concurrent-to-openmp=host -L"$prefix/lib" -Wl,-rpath,"$prefix/lib" \
    "$root/tests/fortran/concurrent.f90" "$output/worker-native.o" -o "$output/native"
timeout 30 "$output/native" > "$output/native.txt"
"$root/bin/llvmnet" --runtime=system -O1 -I"$prefix/include" -c "$root/tests/c/concurrent-worker.c" -o "$output/worker.o"
for optimization in 0 3; do
    target="$output/concurrent-O$optimization"
    "$root/bin/llvmnet" --runtime=system -O"$optimization" -fopenmp -fdo-concurrent-to-openmp=host \
        -c "$root/tests/fortran/concurrent.f90" -o "$target.bc"
    llvm-dis-22 "$target.bc" -o - | grep '@__kmpc_fork_call' > /dev/null
    "$root/bin/llvmnet" --runtime=system --system-library "$library" "$target.bc" "$output/worker.o" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime=system --system-library "$library" --nativeaot "$target.bc" "$output/worker.o" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: system Fortran DO CONCURRENT O%s on four workers in JIT/NativeAOT\n' "$optimization"
done