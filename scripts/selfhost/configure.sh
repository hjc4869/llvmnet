#!/usr/bin/env bash
# Configure LLVM, Clang, MLIR and Flang 22 for llvmnet's portable mode, one build tree for both the tools (clang, flang,
# opt, llvm-ar, llvm-nm) and the compiler's in-process library (build-library.sh). Extra arguments go to CMake.
#
# - Portable mode compiles for x86_64-unknown-none, which predefines no OS macros, while the program runs on musl over the
#   host layer's Linux system calls on every host (ADR-14). LLVM chooses its POSIX and Linux code paths by these macros
#   (endian.h, /proc/self/exe), so the build defines them.
# - -fvisibility=hidden with LLVM_ENABLE_LLVM_C_EXPORT_ANNOTATIONS makes LLVM_C_ABI visibility("default"), so the only
#   default-visibility functions are the C API, which the in-process library exports as libLLVM does. Executables are
#   internalized whatever the visibility.
# - LLVM_APPEND_VC_REV=OFF: the producer string of bitcode the library writes is "22.1.8", as the release's LLVM writes.
# - TableGen comes from the host's LLVM 22; mlir-tblgen is built natively by LLVM's NATIVE sub-build.
# - Precompiled headers are off (Flang's libraries use CMake PCH); Flang's runtime is llvmnet's own (sysroot).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
[[ -x "$native_llvm/llvm-tblgen" ]] || { printf 'The host LLVM 22 tools (llvm-tblgen, clang-tblgen) are needed; set NATIVE_LLVM.\n' >&2; exit 1; }
prepare_source
mkdir -p "$build_dir"
flags="-D__linux__=1 -D__linux=1 -D__gnu_linux__=1 -D__unix__=1 -D__unix=1 -fvisibility=hidden -DLLVM_ENABLE_LLVM_C_EXPORT_ANNOTATIONS"
cmake -G Ninja -S "$source_dir/llvm" -B "$build_dir" \
    -DCMAKE_TOOLCHAIN_FILE="$toolchain_file" \
    -DLLVMNET_RUNTIME=portable \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_FLAGS="$flags" -DCMAKE_CXX_FLAGS="$flags" \
    "-DLLVM_ENABLE_PROJECTS=clang;mlir;flang" -DFLANG_ENABLE_FLANG_RT=OFF \
    -DFLANG_INCLUDE_TESTS=OFF -DMLIR_INCLUDE_TESTS=OFF -DMLIR_INCLUDE_INTEGRATION_TESTS=OFF -DFLANG_INCLUDE_DOCS=OFF \
    -DMLIR_INCLUDE_DOCS=OFF -DFLANG_BUILD_EXAMPLES=OFF -DMLIR_ENABLE_BINDINGS_PYTHON=OFF \
    -DCMAKE_DISABLE_PRECOMPILE_HEADERS=ON \
    "-DCROSS_TOOLCHAIN_FLAGS_NATIVE=-DFLANG_ENABLE_FLANG_RT=OFF;-DCMAKE_C_COMPILER=clang-22;-DCMAKE_CXX_COMPILER=clang++-22" \
    -DLLVM_TARGETS_TO_BUILD=X86 \
    -DLLVM_HOST_TRIPLE=x86_64-pc-linux-gnu \
    -DLLVM_DEFAULT_TARGET_TRIPLE=x86_64-pc-linux-gnu \
    -DLLVM_NATIVE_TOOL_DIR="$native_llvm" \
    -DLLVM_TABLEGEN="$native_llvm/llvm-tblgen" \
    -DCLANG_TABLEGEN="$native_llvm/clang-tblgen" \
    -DLLVM_ENABLE_PIC=OFF \
    -DLLVM_BUILD_LLVM_DYLIB=OFF -DLLVM_LINK_LLVM_DYLIB=OFF \
    -DLLVM_ENABLE_ZLIB=OFF -DLLVM_ENABLE_ZSTD=OFF -DLLVM_ENABLE_LIBXML2=OFF -DLLVM_ENABLE_LIBEDIT=OFF \
    -DLLVM_ENABLE_LIBPFM=OFF -DLLVM_ENABLE_CURL=OFF -DLLVM_ENABLE_HTTPLIB=OFF \
    -DLLVM_ENABLE_THREADS=ON -DLLVM_APPEND_VC_REV=OFF \
    -DLLVM_DISABLE_ASSEMBLY_FILES=ON \
    -DLLVM_INCLUDE_TESTS=OFF -DLLVM_INCLUDE_BENCHMARKS=OFF -DLLVM_INCLUDE_EXAMPLES=OFF -DLLVM_INCLUDE_DOCS=OFF \
    -DLLVM_INCLUDE_UTILS=OFF -DLLVM_BUILD_UTILS=OFF \
    -DCLANG_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_DOCS=OFF -DCLANG_ENABLE_STATIC_ANALYZER=OFF \
    -DCLANG_PLUGIN_SUPPORT=OFF -DLLVM_ENABLE_PLUGINS=OFF \
    "$@"
