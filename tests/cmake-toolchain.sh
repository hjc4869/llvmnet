#!/usr/bin/env bash
# tags: toolchain cpp
# requires: clang++
# The CMake toolchain file (cmake/llvmnet.cmake) on a small C and C++ project: CMake must identify the compilers as
# Clang with 8-byte pointers and known language standards, although it cannot read llvmnet's bitcode objects or
# launchers, and the Generic platform's .obj objects must link. The C program runs in both modes, the C++ program in
# portable mode (system-mode C++ needs the optional libstdc++ archive, docs/user/modes.md).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/source"
cat >"$test_output/source/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.20)
project(probe C CXX)
foreach(language C CXX)
    if(NOT CMAKE_${language}_COMPILER_ID STREQUAL "Clang" OR CMAKE_${language}_COMPILER_VERSION VERSION_LESS 22)
        message(FATAL_ERROR "${language} compiler identified as '${CMAKE_${language}_COMPILER_ID}' '${CMAKE_${language}_COMPILER_VERSION}'")
    endif()
endforeach()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR NOT CMAKE_C_BYTE_ORDER STREQUAL "LITTLE_ENDIAN")
    message(FATAL_ERROR "pointer size '${CMAKE_SIZEOF_VOID_P}', byte order '${CMAKE_C_BYTE_ORDER}'")
endif()
add_library(sum STATIC sum.c)
add_executable(c_probe main.c)
target_link_libraries(c_probe PRIVATE sum)
set_target_properties(c_probe PROPERTIES C_STANDARD 11 C_EXTENSIONS OFF)
if(PROBE_CXX)
    add_executable(cxx_probe main.cpp)
    target_compile_features(cxx_probe PRIVATE cxx_std_20)
    set_target_properties(cxx_probe PROPERTIES CXX_EXTENSIONS OFF)
endif()
CMAKE
printf 'int sum(int a, int b) { return a + b; }\n' >"$test_output/source/sum.c"
printf '#include <stdio.h>\nint sum(int, int);\nint main(void) { printf("C %%ld %%d\\n", (long)__STDC_VERSION__, sum(40, 2)); return 0; }\n' \
    >"$test_output/source/main.c"
printf '#include <iostream>\n#include <span>\nint main() { int v[] = {1, 2, 3}; std::span<int> s(v); std::cout << "C++ " << __cplusplus << " " << s.size() << "\\n"; }\n' \
    >"$test_output/source/main.cpp"
for runtime in portable system; do
    build="$test_output/build-$runtime" cxx=OFF
    if [[ "$runtime" == portable ]]; then cxx=ON; fi
    cmake -G Ninja -S "$test_output/source" -B "$build" -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" -DPROBE_CXX="$cxx" \
        -DCMAKE_C_FLAGS="--runtime=$runtime" -DCMAKE_CXX_FLAGS="--runtime=$runtime" >"$build.log" 2>&1 ||
        { cat "$build.log" >&2; printf 'FAIL: CMake could not configure the project in %s mode\n' "$runtime" >&2; exit 1; }
    cmake --build "$build" >>"$build.log" 2>&1 || { cat "$build.log" >&2; exit 1; }
    test -f "$build/CMakeFiles/sum.dir/sum.c.obj"
    test "$("$build/c_probe")" == "C 201112 42"
    languages=C
    if [[ "$cxx" == ON ]]; then test "$("$build/cxx_probe")" == "C++ 202002 3"; languages="C and C++"; fi
    printf 'PASS: CMake identifies llvmnet as Clang %s and builds, links and runs %s in %s mode\n' \
        "$(sed -n 's/^set(CMAKE_C_COMPILER_VERSION "\(.*\)")$/\1/p' "$build"/CMakeFiles/*/CMakeCCompiler.cmake)" "$languages" "$runtime"
done
