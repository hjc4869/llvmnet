#!/usr/bin/env bash
# tags: core fortran cpp
# requires: flang
# Identical inputs and options must produce byte-identical assemblies and native bridges (BE-05).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/first" "$test_output/second"
for runtime in system portable; do
    for fixture in c/runtime.c cpp/core.cpp fortran/core.f90; do
        name="$(basename "${fixture%.*}")-${fixture%%/*}-$runtime"
        flags=()
        [[ "$fixture" == *.cpp ]] && flags=(-std=c++17)
        for copy in first second; do
            "$llvmnet" --runtime="$runtime" "${flags[@]}" "$root/tests/$fixture" -o "$test_output/$copy/$name.dll"
        done
        cmp "$test_output/first/$name.dll" "$test_output/second/$name.dll"
    done
done
for bridge in "$test_output"/first/*.llvmnet-host.so; do
    cmp "$bridge" "$test_output/second/$(basename "$bridge")"
done
printf 'PASS: repeated C, C++ and Fortran compilations are byte-identical in both ABIs, including native bridges\n'
