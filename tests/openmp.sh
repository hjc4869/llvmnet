#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/openmp"
prefix="${LLVMNET_OPENMP_PREFIX:-$root/artifacts/system-openmp}"
library="${LLVMNET_OPENMP_LIBRARY:-libomp.so.5}"
compiler_args=()
native_link_args=()
if [[ -f "$prefix/include/omp.h" && -f "$prefix/lib/libomp.so.5" ]]; then
    library="${LLVMNET_OPENMP_LIBRARY:-$prefix/lib/libomp.so.5}"
    compiler_args+=(-I"$prefix/include")
    native_link_args+=(-L"$prefix/lib" -Wl,-rpath,"$prefix/lib")
    export LD_LIBRARY_PATH="$prefix/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
export OMP_NUM_THREADS=4 OMP_THREAD_LIMIT=4 OMP_DYNAMIC=FALSE
clang-22 -fopenmp "${compiler_args[@]}" "${native_link_args[@]}" -O1 "$root/tests/c/openmp.c" -o "$output/native"
timeout 30 "$output/native" > "$output/native.txt"
for optimization in 0 3; do
    target="$output/openmp-O$optimization"
    "$root/bin/llvmnet" --runtime=system --system-library "$library" -fopenmp "${compiler_args[@]}" -O"$optimization" "$root/tests/c/openmp.c" -o "$target.dll"
    timeout 30 dotnet "$target.dll" > "$target.txt"
    diff -u "$output/native.txt" "$target.txt"
    "$root/bin/llvmnet" --runtime=system --system-library "$library" --nativeaot -fopenmp "${compiler_args[@]}" -O"$optimization" "$root/tests/c/openmp.c" -o "$target-aot"
    timeout 30 "$target-aot" > "$target-aot.txt"
    diff -u "$output/native.txt" "$target-aot.txt"
    printf 'PASS: system OpenMP O%s with four workers in JIT/NativeAOT\n' "$optimization"
done