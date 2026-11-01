#!/usr/bin/env bash
# tags: toolchain cpp
# requires: clang++
# The CMake toolchain file (cmake/llvmnet.cmake) and its platform (cmake/Platform/LlvmNet.cmake) on a small C and C++
# project. CMake must identify the compilers as Clang with 8-byte pointers and known language standards, although it
# cannot read llvmnet's bitcode objects or launchers. Probes link and run programs (TC-07), so their results must equal
# those of a native clang-22 configure of the same project. UNIX is set; LLVMNET_RUNTIME selects the mode, also in a
# directory that resets CMAKE_C_FLAGS; a SHARED target is an archive under its Linux names that contains its static
# dependencies, so a SHARED and a STATIC target with one output name do not collide. The C programs run in both modes,
# the C++ program in portable mode (system-mode C++ needs the optional libstdc++ archive, docs/user/modes.md).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/source/flags"
cat >"$test_output/source/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.20...3.31)
project(probe C CXX)
foreach(language C CXX)
    if(NOT CMAKE_${language}_COMPILER_ID STREQUAL "Clang" OR CMAKE_${language}_COMPILER_VERSION VERSION_LESS 22)
        message(FATAL_ERROR "${language} compiler identified as '${CMAKE_${language}_COMPILER_ID}' '${CMAKE_${language}_COMPILER_VERSION}'")
    endif()
endforeach()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR NOT CMAKE_C_BYTE_ORDER STREQUAL "LITTLE_ENDIAN" OR NOT UNIX)
    message(FATAL_ERROR "pointer size '${CMAKE_SIZEOF_VOID_P}', byte order '${CMAKE_C_BYTE_ORDER}', UNIX '${UNIX}'")
endif()
# Each probe's result, written to probes.txt and compared with a native configure.
include(CheckCSourceCompiles)
include(CheckCSourceRuns)
include(CheckFunctionExists)
include(CheckLibraryExists)
include(CheckSymbolExists)
include(CheckTypeSize)
check_function_exists(gmtime_r HAVE_GMTIME_R)
check_function_exists(llvmnet_no_such_function HAVE_NO_SUCH_FUNCTION)
check_library_exists(m llvmnet_no_such_function "" HAVE_NO_SUCH_LIBRARY_FUNCTION)
check_symbol_exists(strlcpy "string.h" HAVE_STRLCPY)
# brotli's test for Emscripten: only the missing main makes it fail.
check_c_source_compiles("#if defined(__EMSCRIPTEN__)\nint main(void) { return 0; }\n#endif" LOOKS_LIKE_EMSCRIPTEN)
check_type_size("size_t" SIZEOF_SIZE_T)
check_type_size("long double" SIZEOF_LONG_DOUBLE)
check_c_source_runs("int main(void) { return sizeof(long) != 8; }" LONG_IS_64_BITS)
find_package(Threads)
find_package(Iconv)
set(results "")
foreach(variable HAVE_GMTIME_R HAVE_NO_SUCH_FUNCTION HAVE_NO_SUCH_LIBRARY_FUNCTION HAVE_STRLCPY LOOKS_LIKE_EMSCRIPTEN
        HAVE_SIZEOF_SIZE_T SIZEOF_SIZE_T SIZEOF_LONG_DOUBLE LONG_IS_64_BITS Threads_FOUND CMAKE_USE_PTHREADS_INIT
        Iconv_FOUND Iconv_IS_BUILT_IN)
    string(APPEND results "${variable}=${${variable}}\n")
endforeach()
file(WRITE "${CMAKE_BINARY_DIR}/probes.txt" "${results}")
add_library(sum STATIC sum.c)
add_executable(c_probe main.c)
target_link_libraries(c_probe PRIVATE sum)
set_target_properties(c_probe PROPERTIES C_STANDARD 11 C_EXTENSIONS OFF)
# A SHARED and a STATIC library with one output name, as zlib and libjpeg-turbo build them; both link sum PRIVATE.
add_library(twice SHARED twice.c)
add_library(twice_static STATIC twice.c)
set_target_properties(twice twice_static PROPERTIES OUTPUT_NAME twice)
set_target_properties(twice PROPERTIES VERSION 1.2.3 SOVERSION 1)
target_link_libraries(twice PRIVATE sum)
target_link_libraries(twice_static PRIVATE sum)
add_executable(static_probe twice-main.c)
target_link_libraries(static_probe PRIVATE twice_static)
if(PROBE_CXX)
    add_executable(cxx_probe main.cpp)
    target_compile_features(cxx_probe PRIVATE cxx_std_20)
    set_target_properties(cxx_probe PROPERTIES CXX_EXTENSIONS OFF)
endif()
add_subdirectory(flags)
CMAKE
# zlib's test directory does this; the mode must survive it.
cat >"$test_output/source/flags/CMakeLists.txt" <<'CMAKE'
set(CMAKE_C_FLAGS "")
add_executable(mode mode.c)
CMAKE
printf 'int sum(int a, int b) { return a + b; }\n' >"$test_output/source/sum.c"
printf '#include <stdio.h>\nint sum(int, int);\nint main(void) { printf("C %%ld %%d\\n", (long)__STDC_VERSION__, sum(40, 2)); return 0; }\n' \
    >"$test_output/source/main.c"
printf 'int sum(int, int);\nint twice(int x) { return sum(x, x); }\n' >"$test_output/source/twice.c"
printf '#include <stdio.h>\nint twice(int);\nint main(void) { printf("twice %%d\\n", twice(21)); return 0; }\n' >"$test_output/source/twice-main.c"
printf '#include <stdio.h>\nint main(void) {\n#ifdef __LLVMNET_PORTABLE__\n    puts("portable");\n#else\n    puts("system");\n#endif\n    return 0;\n}\n' \
    >"$test_output/source/flags/mode.c"
printf '#include <iostream>\n#include <span>\nint main() { int v[] = {1, 2, 3}; std::span<int> s(v); std::cout << "C++ " << __cplusplus << " " << s.size() << "\\n"; }\n' \
    >"$test_output/source/main.cpp"
native="$test_output/build-native"
cmake -G Ninja -S "$test_output/source" -B "$native" -DCMAKE_C_COMPILER=clang-22 -DCMAKE_CXX_COMPILER=clang++-22 >"$native.log" 2>&1 ||
    { cat "$native.log" >&2; printf 'FAIL: CMake could not configure the project natively\n' >&2; exit 1; }
for runtime in portable system; do
    build="$test_output/build-$runtime" cxx=OFF
    if [[ "$runtime" == portable ]]; then cxx=ON; fi
    cmake -G Ninja -S "$test_output/source" -B "$build" -DCMAKE_TOOLCHAIN_FILE="$root/cmake/llvmnet.cmake" -DPROBE_CXX="$cxx" \
        -DLLVMNET_RUNTIME="$runtime" >"$build.log" 2>&1 ||
        { cat "$build.log" >&2; printf 'FAIL: CMake could not configure the project in %s mode\n' "$runtime" >&2; exit 1; }
    if ! diff -u "$native/probes.txt" "$build/probes.txt"; then
        printf 'FAIL: probe results in %s mode differ from a native configure; see %s\n' "$runtime" "$build/CMakeFiles/CMakeConfigureLog.yaml" >&2
        exit 1
    fi
    cmake --build "$build" >>"$build.log" 2>&1 || { cat "$build.log" >&2; exit 1; }
    test -f "$build/CMakeFiles/sum.dir/sum.c.o"
    test "$("$build/c_probe")" == "C 201112 42"
    test "$("$build/static_probe")" == "twice 42"
    test "$("$build/flags/mode")" == "$runtime"
    # The SHARED library: an archive under its Linux names, beside the STATIC one, with the member of its PRIVATE dependency.
    test -L "$build/libtwice.so" && test -L "$build/libtwice.so.1" && test -f "$build/libtwice.so.1.2.3" && test -f "$build/libtwice.a"
    test "$(llvm-ar-22 t "$build/libtwice.so.1.2.3" | LC_ALL=C sort | tr '\n' ' ')" == "sum.c.o twice.c.o "
    languages=C
    if [[ "$cxx" == ON ]]; then test "$("$build/cxx_probe")" == "C++ 202002 3"; languages="C and C++"; fi
    printf 'PASS: CMake identifies llvmnet as Clang %s; %d probes agree with native; builds, links and runs %s in %s mode\n' \
        "$(sed -n 's/^set(CMAKE_C_COMPILER_VERSION "\(.*\)")$/\1/p' "$build"/CMakeFiles/*/CMakeCCompiler.cmake)" \
        "$(wc -l <"$build/probes.txt")" "$languages" "$runtime"
done
