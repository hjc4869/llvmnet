#!/usr/bin/env bash
# tags: core aot
# requires: python3 cmake
# NX-09: a native extension generated from C headers instead of a registry. tests/extensions/glfw/extension.json reads
# GLFW 3.4's glfw3.h (pinned) through the JSON AST that llvmnet's Clang produces for the portable target: 119 functions,
# the callback typedefs (23 with reverse thunks), the GLFWallocator structure of callbacks; glfwGetProcAddress, which
# returns a host function pointer, fails when called. GLFW itself is built natively from the same source with only its
# headless null platform, and tests/extensions/glfw.c (allocator, error, window and framebuffer callbacks, the callbacks
# the setters return, the null monitor's video mode and gamma ramp) must print tests/extensions/glfw.expected natively,
# in portable mode under the JIT and as NativeAOT, and in system mode.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures="$root/tests/extensions"
expected="$fixtures/glfw.expected"
glfw="$("$root/scripts/fetch-sources.sh" --path glfw)"
cmake -S "$glfw" -B "$test_output/glfw-build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-22 -DBUILD_SHARED_LIBS=ON \
    -DGLFW_BUILD_X11=OFF -DGLFW_BUILD_WAYLAND=OFF -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_DOCS=OFF \
    >"$test_output/glfw-cmake.log" 2>&1 || { cat "$test_output/glfw-cmake.log" >&2; exit 1; }
ninja -C "$test_output/glfw-build" >"$test_output/glfw-ninja.log" 2>&1 || { cat "$test_output/glfw-ninja.log" >&2; exit 1; }
native="$test_output/glfw-build/src"
clang-22 -O1 -I"$glfw/include" "$fixtures/glfw.c" -L"$native" -l:libglfw.so.3 -o "$test_output/glfw-native"
LD_LIBRARY_PATH="$native" "$test_output/glfw-native" | diff -u "$expected" -

toolchain="$test_output/toolchain"
extension_root "$toolchain"
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" \
    "$fixtures/glfw/extension.json" 2>"$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'glfw 3.4.0: 119 entry points, 1 that fail when called, 0 left out, 23 callback types with reverse thunks' "$test_output/build.log" ||
    { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified 120 stubs against the C prototypes llvmnet compiled' "$test_output/build.log"
export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$fixtures/glfw.c" -lglfw -o "$test_output/jit/glfw.dll"
LLVMNET_EXT_GLFW_DIR="$native" dotnet "$test_output/jit/glfw.dll" | diff -u "$expected" -
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$fixtures/glfw.c" -lglfw -o "$test_output/aot/glfw" >"$test_output/aot.log" 2>&1 || { cat "$test_output/aot.log" >&2; exit 1; }
LLVMNET_EXT_GLFW_DIR="$native" "$test_output/aot/glfw" | diff -u "$expected" -
LIBRARY_PATH="$native" LD_LIBRARY_PATH="$native" "$llvmnet" --runtime=system -O1 -I"$glfw/include" "$fixtures/glfw.c" --extension=glfw \
    -o "$test_output/system/glfw.dll"
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/glfw.dll" | diff -u "$expected" -
printf 'PASS: an extension generated from glfw3.h prints what native GLFW prints (JIT, NativeAOT, system mode)\n'

# A header's variadic functions are left out with the reason; a structure passed by value stops the generation with the
# function and the reason, until the description excludes it (structures by value are NX-07).
case="$test_output/variadic"
mkdir -p "$case/include"
cat >"$case/include/variadic.h" <<'HEADER'
typedef struct { int x, y; } point;
int var_print(const char *format, ...);
int var_add(int a, int b);
int var_point(point p);
HEADER
cat >"$case/extension.json" <<'JSON'
{ "name": "variadic", "version": "1.0.0",
  "registries": [{ "kind": "headers", "include-path": "include", "header": "variadic.h", "include": ["var_*"], "exclude": ["var_point"],
                   "apis": ["variadic"], "library": "Variadic" }],
  "libraries": [{ "name": "Variadic", "linux": ["libvariadic.so"], "dlopen": ["libvariadic.so"] }] }
JSON
python3 -I "$root/scripts/ext-gen.py" generate "$case/extension.json" --output "$case/out" --llvmnet-version 0.0.0 --compiler "$llvmnet" \
    >"$case/generate.log"
grep -q '^variadic 1.0.0: 1 entry points, 0 that fail when called, 1 left out' "$case/generate.log" || { cat "$case/generate.log" >&2; exit 1; }
grep -q "left out var_print: it is variadic" "$case/generate.log"
cat >"$case/including.json" <<'JSON'
{ "name": "variadic", "version": "1.0.0",
  "registries": [{ "kind": "headers", "include-path": "include", "header": "variadic.h", "include": ["var_*"],
                   "apis": ["variadic"], "library": "Variadic" }],
  "libraries": [{ "name": "Variadic", "linux": ["libvariadic.so"], "dlopen": ["libvariadic.so"] }] }
JSON
status=0
python3 -I "$root/scripts/ext-gen.py" generate "$case/including.json" --output "$case/out2" --llvmnet-version 0.0.0 --compiler "$llvmnet" \
    >/dev/null 2>"$case/error.txt" || status=$?
if [[ "$status" -ne 1 ]] || ! grep -q 'var_point in variadic.h: .* is passed by value' "$case/error.txt"; then
    printf 'FAIL: var_point was not rejected:\n' >&2
    cat "$case/error.txt" >&2
    exit 1
fi
printf 'PASS: variadic functions are left out and structures by value rejected, with the reason\n'
