#!/usr/bin/env bash
# tags: core aot fortran
# requires: flang
# OpenMP in portable mode (FT-08): LLVM's libomp from the sysroot, on musl's pthreads over managed threads.
# tests/c/openmp.c checks parallel regions, dynamic scheduling, a reduction, critical sections and barriers on four
# workers under JIT and NativeAOT; the default team size follows Environment.ProcessorCount (DOTNET_PROCESSOR_COUNT
# in the test) unless OMP_NUM_THREADS is set; and Flang's DO CONCURRENT lowered to OpenMP runs on four workers.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
mkdir -p "$test_output"
expected='OpenMP: four workers, dynamic scheduling, reduction and barriers passed'
export OMP_NUM_THREADS=4 OMP_THREAD_LIMIT=4 OMP_DYNAMIC=FALSE
for optimization in 0 3; do
    "$llvmnet" --runtime=portable -fopenmp -O"$optimization" "$root/tests/c/openmp.c" -o "$test_output/openmp-O$optimization.dll"
    [[ "$(timeout 120 dotnet "$test_output/openmp-O$optimization.dll")" == "$expected" ]]
done
"$llvmnet" --runtime=portable -fopenmp -O1 --nativeaot "$root/tests/c/openmp.c" -o "$test_output/openmp-aot" >"$test_output/aot.log"
[[ "$(timeout 120 "$test_output/openmp-aot")" == "$expected" ]]
printf 'PASS: portable OpenMP with four workers under JIT (O0, O3) and NativeAOT\n'
"$llvmnet" --runtime=portable -fopenmp -O1 "$root/tests/c/openmp-count.c" -o "$test_output/count.dll"
[[ "$(env -u OMP_NUM_THREADS -u OMP_THREAD_LIMIT DOTNET_PROCESSOR_COUNT=3 timeout 120 dotnet "$test_output/count.dll")" == "3 3" ]]
[[ "$(env -u OMP_THREAD_LIMIT OMP_NUM_THREADS=5 DOTNET_PROCESSOR_COUNT=3 timeout 120 dotnet "$test_output/count.dll")" == "5 5" ]]
printf 'PASS: the default team size is the processor count .NET reports\n'
# Flang lowers DO CONCURRENT to OpenMP; the C worker reports which thread ran each iteration.
"$llvmnet" --runtime=portable -O1 -c "$root/tests/c/concurrent-worker.c" -o "$test_output/worker.o"
"$llvmnet" --runtime=portable -O1 -fopenmp -fdo-concurrent-to-openmp=host "$root/tests/fortran/concurrent.f90" "$test_output/worker.o" \
    -o "$test_output/concurrent.dll" 2>"$test_output/concurrent.log"
[[ "$(timeout 120 dotnet "$test_output/concurrent.dll")" == 'Fortran DO CONCURRENT: four workers and nested loops passed' ]]
printf 'PASS: portable Fortran DO CONCURRENT on four workers\n'
