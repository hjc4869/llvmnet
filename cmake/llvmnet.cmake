# CMake toolchain file for llvmnet: cmake -DCMAKE_TOOLCHAIN_FILE=<this file>. It works from a checkout (cmake/) and from
# an installed toolchain (share/llvmnet/cmake/). -DLLVMNET_RUNTIME=system selects system mode (portable is the default);
# pass -DCMAKE_C_COMPILER/-DCMAKE_CXX_COMPILER to use other drivers.
#
# The target platform is Platform/LlvmNet.cmake beside this file: UNIX without the host's directories, with SHARED
# libraries built as bitcode archives.
set(CMAKE_SYSTEM_NAME LlvmNet)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_LIST_DIR}")
get_filename_component(LLVMNET_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
if(NOT EXISTS "${LLVMNET_ROOT}/bin/llvmnet")
    get_filename_component(LLVMNET_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)
endif()
# The mode is an argument of the compiler command itself, so it reaches every compile, link and probe, also in projects
# that set CMAKE_C_FLAGS themselves. A compiler given with -DCMAKE_C_COMPILER carries its own mode.
set(LLVMNET_RUNTIME "portable" CACHE STRING "llvmnet runtime mode: portable or system")
if(NOT LLVMNET_RUNTIME MATCHES "^(portable|system)$")
    message(FATAL_ERROR "llvmnet.cmake: LLVMNET_RUNTIME is '${LLVMNET_RUNTIME}'; use portable or system")
endif()
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES LLVMNET_RUNTIME)
if(NOT CMAKE_C_COMPILER)
    set(CMAKE_C_COMPILER "${LLVMNET_ROOT}/bin/llvmnet")
    if(EXISTS "${LLVMNET_ROOT}/bin/llvmnet-clang")
        set(CMAKE_C_COMPILER "${LLVMNET_ROOT}/bin/llvmnet-clang")
    endif()
    list(APPEND CMAKE_C_COMPILER "--runtime=${LLVMNET_RUNTIME}")
endif()
if(NOT CMAKE_CXX_COMPILER)
    set(CMAKE_CXX_COMPILER "${LLVMNET_ROOT}/bin/llvmnet")
    if(EXISTS "${LLVMNET_ROOT}/bin/llvmnet-clang++")
        set(CMAKE_CXX_COMPILER "${LLVMNET_ROOT}/bin/llvmnet-clang++")
    endif()
    list(APPEND CMAKE_CXX_COMPILER "--runtime=${LLVMNET_RUNTIME}")
endif()
# An installed toolchain's llvmnet-ar, -ranlib and -nm use its bundled LLVM when it has one.
foreach(_llvmnet_tool ar ranlib nm)
    string(TOUPPER "${_llvmnet_tool}" _llvmnet_variable)
    if(EXISTS "${LLVMNET_ROOT}/bin/llvmnet-${_llvmnet_tool}")
        set(CMAKE_${_llvmnet_variable} "${LLVMNET_ROOT}/bin/llvmnet-${_llvmnet_tool}" CACHE FILEPATH "LLVM bitcode ${_llvmnet_tool}")
    else()
        set(CMAKE_${_llvmnet_variable} "llvm-${_llvmnet_tool}-22" CACHE FILEPATH "LLVM bitcode ${_llvmnet_tool}")
    endif()
endforeach()
# Probes link (and try_run runs) real programs, as with a native compiler, so a probe for a function or a feature that
# does not exist fails. Programs are a launcher beside a managed image; the launcher runs as it is.
set(CMAKE_CROSSCOMPILING_EMULATOR env)
set(CMAKE_C_FLAGS_INIT "-O1 -fno-vectorize -fno-slp-vectorize -fno-stack-protector")
set(CMAKE_CXX_FLAGS_INIT "-O1 -fno-vectorize -fno-slp-vectorize -fno-stack-protector")

# CMake identifies a compiler by reading INFO strings out of an object a probe compiles, and llvmnet's objects are LLVM
# bitcode, where those strings cannot be read. llvmnet is Clang with one 64-bit little-endian data layout in both modes,
# so state what the probes would have found. The Clang version comes from the driver once and is passed on to the
# try_compile projects.
if(NOT LLVMNET_CLANG_VERSION)
    execute_process(COMMAND ${CMAKE_C_COMPILER} --version OUTPUT_VARIABLE _llvmnet_version ERROR_QUIET)
    if(_llvmnet_version MATCHES "LLVM ([0-9]+\\.[0-9]+\\.[0-9]+)")
        set(LLVMNET_CLANG_VERSION "${CMAKE_MATCH_1}")
    else()
        message(FATAL_ERROR "llvmnet.cmake: '${CMAKE_C_COMPILER} --version' did not report an LLVM version")
    endif()
endif()
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES LLVMNET_CLANG_VERSION)
foreach(_llvmnet_language C CXX)
    set(CMAKE_${_llvmnet_language}_COMPILER_ID_RUN TRUE)
    set(CMAKE_${_llvmnet_language}_COMPILER_ID Clang)
    set(CMAKE_${_llvmnet_language}_COMPILER_VERSION "${LLVMNET_CLANG_VERSION}")
    set(CMAKE_${_llvmnet_language}_COMPILER_FRONTEND_VARIANT GNU)
    set(CMAKE_${_llvmnet_language}_PLATFORM_ID Linux)
    set(CMAKE_${_llvmnet_language}_SIZEOF_DATA_PTR_DEFAULT 8)
    set(CMAKE_${_llvmnet_language}_BYTE_ORDER LITTLE_ENDIAN)
    set(CMAKE_${_llvmnet_language}_COMPILER_ABI ELF)
    set(CMAKE_${_llvmnet_language}_EXTENSIONS_COMPUTED_DEFAULT ON)
endforeach()
set(CMAKE_C_STANDARD_COMPUTED_DEFAULT 17)
set(CMAKE_CXX_STANDARD_COMPUTED_DEFAULT 17)
# Projects whose policies reach CMake 3.28 (CMP0155) scan C++20 sources for module imports with clang-scan-deps, which
# cannot parse llvmnet's command lines (and is not looked for, since the compiler is not identified by a probe).
set(CMAKE_CXX_SCAN_FOR_MODULES OFF)
# check_type_size, CMake's ABI check and FindOpenMP read INFO strings out of the program a probe links, which for llvmnet
# are in the managed image beside the launcher; in try_compile projects the link copies them into the launcher
# (llvmnet-rules.cmake).
get_property(_llvmnet_try_compile GLOBAL PROPERTY IN_TRY_COMPILE)
if(_llvmnet_try_compile)
    foreach(_llvmnet_language C CXX)
        set(CMAKE_${_llvmnet_language}_LINK_EXECUTABLE
            "<CMAKE_${_llvmnet_language}_COMPILER> <FLAGS> <CMAKE_${_llvmnet_language}_LINK_FLAGS> <LINK_FLAGS> <OBJECTS> -o <TARGET> <LINK_LIBRARIES>"
            "\"${CMAKE_COMMAND}\" -DLLVMNET_LAUNCHER=<TARGET> -P \"${CMAKE_CURRENT_LIST_DIR}/llvmnet-rules.cmake\"")
    endforeach()
endif()
