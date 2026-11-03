#!/usr/bin/env bash
# tags: core aot
# requires: python3 egl
# NX-05: the gles native extension (extensions/gles) against the host's Mesa. The extension is generated from the
# pinned Khronos registries into a toolchain root of its own (scripts/build-extensions.sh); tests/extensions/gles.c,
# linked with -lEGL -lGLESv2, renders headless (Mesa's surfaceless platform), reads pixels back, takes a GL_KHR_debug
# callback from the driver, uses GLES 3, and loads EGL through dlopen as ANGLE does. The native build, the portable
# image under the JIT on Mesa's software renderer (llvmpipe, which every runner has) and on the default device, the
# NativeAOT executable and the system-mode image must all print tests/extensions/gles.expected.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures="$root/tests/extensions"
expected="$fixtures/gles.expected"
toolchain="$test_output/toolchain"
extension_root "$toolchain"
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" gles \
    2>"$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified [0-9]* stubs against the C prototypes llvmnet compiled' "$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
include="$toolchain/sysroot/dotnet64-v2/ext/gles/include"
# run <label> <command...>: standard output must be the expected lines; standard error keeps the renderer for the log.
run() {
    local label="$1"
    shift
    if ! "$@" >"$test_output/$label.txt" 2>"$test_output/$label.err"; then
        printf 'FAIL: %s exited with an error\n' "$label" >&2
        cat "$test_output/$label.txt" "$test_output/$label.err" >&2
        exit 1
    fi
    diff -u "$expected" "$test_output/$label.txt" || { printf 'FAIL: %s differs\n' "$label" >&2; cat "$test_output/$label.err" >&2; exit 1; }
    grep -m1 '^GL_RENDERER' "$test_output/$label.err" | sed "s/^/$label: /"
}
clang-22 -O1 -I"$include" "$fixtures/gles.c" -l:libEGL.so.1 -l:libGLESv2.so.2 -o "$test_output/gles-native"
run native-llvmpipe env LIBGL_ALWAYS_SOFTWARE=1 "$test_output/gles-native"
export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$fixtures/gles.c" -lEGL -lGLESv2 -o "$test_output/jit/gles.dll"
test -f "$test_output/jit/LlvmNet.Ext.Gles.dll"
run jit-llvmpipe env LIBGL_ALWAYS_SOFTWARE=1 dotnet "$test_output/jit/gles.dll"
run jit-default dotnet "$test_output/jit/gles.dll"
dotnet "$checks" inspect "$test_output/jit/gles.dll" | grep -qx 'llvmnet.extensions: gles/1.0.0'
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$fixtures/gles.c" -lEGL -lGLESv2 -o "$test_output/aot/gles" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
run aot-llvmpipe env LIBGL_ALWAYS_SOFTWARE=1 "$test_output/aot/gles"
# glGetError and eglGetError without the GC transition, which Mesa allows (NX-11).
run jit-non-blocking env LIBGL_ALWAYS_SOFTWARE=1 LLVMNET_EXT_GLES_NON_BLOCKING=1 dotnet "$test_output/jit/gles.dll"
run aot-non-blocking env LIBGL_ALWAYS_SOFTWARE=1 LLVMNET_EXT_GLES_NON_BLOCKING=1 "$test_output/aot/gles"
# System mode links the host's libraries directly; the extension supplies only the library names.
"$llvmnet" --runtime=system -O1 -I"$include" "$fixtures/gles.c" --extension=gles -o "$test_output/system/gles.dll"
run system-llvmpipe env LIBGL_ALWAYS_SOFTWARE=1 dotnet "$test_output/system/gles.dll"
printf 'PASS: the gles extension renders, calls back and loads through dlopen as the native build does (JIT, NativeAOT, system mode)\n'
