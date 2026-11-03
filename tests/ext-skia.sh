#!/usr/bin/env bash
# tags: optional aot
# requires: egl xvfb skia
# timeout: 3600
# NX-13: the AvaloniaStatic scenario end to end. Skia, with the SkiaSharp C API (LLVMNET_SKIA_DIR: a SkiaSharp-synced
# Skia checkout with bin/gn), is built twice with its Ganesh GL backend and EGL: in portable mode, where EGL and GLES
# are the gles extension, and natively with clang-22 against the host's Mesa. tests/extensions/skia/skia-gl.c assembles
# Skia's GrGLInterface from eglGetProcAddress, as Avalonia does through SkiaSharp, and draws an animated UI-like scene
# into a window that the .NET host tests/extensions/skia/host/ creates on an Xvfb server and owns. The renderer runs
# natively and in portable mode in five forms: in the host's process under the JIT, as a JIT child process, as a
# NativeAOT child process, and with the host as one NativeAOT executable (host-aot/), as an Avalonia application with
# AvaloniaStatic is one. Every form must print what the native renderer prints (the last frame's hash, the CPU raster
# reference, the pixels of flat regions), and the window, read back by the host, must show that frame.
#
#   tests/ext-skia.sh [--bundle <dir>]    --bundle also writes the Windows and macOS run (tests/extensions/skia/run.sh)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
bundle=""
if [[ "${1:-}" == --bundle ]]; then bundle="$(realpath -m "$2")"; fi
skia="${LLVMNET_SKIA_DIR:?LLVMNET_SKIA_DIR names a Skia checkout with the SkiaSharp C API}"
fixture="$root/tests/extensions/skia"
rm -rf "$test_output"
mkdir -p "$test_output/bin"
bash "$root/scripts/build-extensions.sh" gles >/dev/null 2>"$test_output/extensions.log" || { cat "$test_output/extensions.log" >&2; exit 1; }
gles_include="$root/sysroot/dotnet64-v2/ext/gles/include"

# Skia's GN build adds -rdynamic to executables, which llvmnet rejects; only the libraries are built here.
for driver in clang clang++; do
    cat >"$test_output/bin/llvmnet-$driver" <<WRAPPER
#!/usr/bin/env bash
arguments=()
for argument in "\$@"; do [[ "\$argument" == -rdynamic ]] || arguments+=("\$argument"); done
LLVMNET_DRIVER=$driver exec "$root/bin/llvmnet" "\${arguments[@]}"
WRAPPER
    chmod +x "$test_output/bin/llvmnet-$driver"
done
common='is_official_build = false
is_debug = false
skia_disable_tracing = true
is_static_skiasharp = true
skia_enable_pdf = false
skia_enable_skottie = false
skia_enable_tools = false
skia_enable_ganesh = true
skia_use_gl = true
skia_use_egl = true
skia_use_vulkan = false
skia_use_metal = false
skia_use_xps = false
skia_use_dng_sdk = false
skia_use_piex = false
skia_use_harfbuzz = false
skia_use_icu = false
skia_use_partition_alloc = false
skia_use_freetype = true
skia_use_fontconfig = false
skia_use_perfetto = false
skia_use_x11 = false
skia_use_lua = false
skia_use_system_expat = false
skia_use_system_freetype2 = false
skia_use_system_libjpeg_turbo = false
skia_use_system_libpng = false
skia_use_system_libwebp = false
skia_use_system_zlib = false
target_os = "linux"
target_cpu = "generic"
'
# target_cpu "generic" keeps Skia's x86 code paths out of both builds; the native build also hides the x86 CPU macros
# from Skia (skia-native-paths.h), so that both take the same portable code paths and draw the same pixels.
mkdir -p "$test_output/skia-portable" "$test_output/skia-native"
cat >"$test_output/skia-portable/args.gn" <<ARGS
${common}cc = "$test_output/bin/llvmnet-clang"
cxx = "$test_output/bin/llvmnet-clang++"
ar = "llvm-ar-22"
host_cc = "clang-22"
host_cxx = "clang++-22"
host_ar = "llvm-ar-22"
extra_cflags = ["-DSKIA_C_DLL", "-DHAVE_SYSCALL_GETRANDOM", "-DXML_DEV_URANDOM", "-DSK_BUILD_FOR_UNIX", "-g0"]
extra_cflags_cc = ["-frtti"]
ARGS
cat >"$test_output/skia-native-paths.h" <<HEADER
#ifdef __cplusplus
#include "$skia/include/private/SkFeatures.h"
#undef SK_CPU_X86
#undef SK_CPU_X64_LEVEL
#endif
HEADER
cat >"$test_output/skia-native/args.gn" <<ARGS
${common}cc = "clang-22"
cxx = "clang++-22"
ar = "llvm-ar-22"
extra_cflags = ["-DSKIA_C_DLL", "-DHAVE_SYSCALL_GETRANDOM", "-DXML_DEV_URANDOM", "-fno-vectorize", "-fno-slp-vectorize", "-g0", "-I$gles_include"]
extra_cflags_cc = ["-frtti", "-include", "$test_output/skia-native-paths.h"]
ARGS
jobs="${LLVMNET_TEST_JOBS:-8}"
for mode in portable native; do
    (cd "$skia" && bin/gn gen "$test_output/skia-$mode" >"$test_output/skia-$mode.gn.log" 2>&1) || { cat "$test_output/skia-$mode.gn.log" >&2; exit 1; }
    ninja -C "$test_output/skia-$mode" -j "$jobs" skia SkiaSharp >"$test_output/skia-$mode.log" 2>&1 || { tail -40 "$test_output/skia-$mode.log" >&2; exit 1; }
done
archives() { local directory="$1"; printf '%s\n' "$directory/libSkiaSharp.a" "$directory/libskia.a"; ls "$directory"/*.a | grep -v '/libSkiaSharp\.a$\|/libskia\.a$'; }
mapfile -t native_archives < <(archives "$test_output/skia-native")
mapfile -t portable_archives < <(archives "$test_output/skia-portable")

out="$test_output/out"
mkdir -p "$out/aot"
clang-22 -O2 -I"$skia" -I"$gles_include" -c "$fixture/skia-gl.c" -o "$out/native.o"
clang++-22 "$out/native.o" "${native_archives[@]}" -l:libEGL.so.1 -l:libGLESv2.so.2 -lpthread -ldl -o "$out/skia-gl-native"
"$root/bin/llvmnet-clang" -O2 -I"$skia" -c "$fixture/skia-gl.c" -o "$out/portable.o"
"$root/bin/llvmnet-clang++" -O2 "$out/portable.o" "${portable_archives[@]}" -lEGL -lGLESv2 -o "$out/jit/skia-gl.dll"
"$root/bin/llvmnet-clang++" -O2 "$out/portable.o" "${portable_archives[@]}" -lEGL -lGLESv2 --nativeaot -o "$out/aot/skia-gl" \
    >"$out/aot.log" 2>&1 || { cat "$out/aot.log" >&2; exit 1; }
dotnet build "$fixture/host/SkiaHost.csproj" -c Release --nologo -v quiet -clp:ErrorsOnly --artifacts-path "$test_output/msbuild" \
    -o "$out/host" >"$out/host.log" 2>&1 || { cat "$out/host.log" >&2; exit 1; }
dotnet publish "$fixture/host-aot/SkiaHostAot.csproj" -c Release -r linux-x64 -p:LlvmNetImage="$out/jit/skia-gl.dll" --nologo -v quiet \
    --artifacts-path "$test_output/msbuild-aot" -o "$out/host-aot" >"$out/host-aot.log" 2>&1 || { cat "$out/host-aot.log" >&2; exit 1; }

# Each form gets an Xvfb server of its own; Mesa renders with llvmpipe there, so every form must draw the same pixels.
run() {
    local name="$1" status=0
    shift
    xvfb-run -a -s "-screen 0 1024x768x24" "$@" >"$out/$name.out" 2>"$out/$name.err" || status=$?
    if [[ "$status" -ne 0 ]]; then printf 'FAIL: %s exited with %d\n' "$name" "$status" >&2; cat "$out/$name.out" "$out/$name.err" >&2; exit 1; fi
    local frame window
    frame="$(sed -n 's/^frame hash: //p' "$out/$name.out")"
    window="$(sed -n 's/^host: the window shows hash //p' "$out/$name.out")"
    if [[ -z "$frame" || "$frame" != "$window" ]]; then
        printf 'FAIL: %s rendered frame %s, but the window shows %s\n' "$name" "${frame:-(none)}" "${window:-(nothing)}" >&2; cat "$out/$name.out" >&2; exit 1
    fi
    grep -v '^host: ' "$out/$name.out" >"$out/$name.txt"
    sed -n 's/^time: //p' "$out/$name.err" | sed "s/^/$name: /" >>"$out/times.txt"
}
run native dotnet "$out/host/SkiaHost.dll" --exec "$out/skia-gl-native"
grep -q '^surface: x11 480x360, ' "$out/native.txt" && grep -q '^GPU against raster: close ' "$out/native.txt" || { cat "$out/native.txt" >&2; exit 1; }
run jit-in-process dotnet "$out/host/SkiaHost.dll" --image "$out/jit/skia-gl.dll"
run jit-child dotnet "$out/host/SkiaHost.dll" --exec dotnet "$out/jit/skia-gl.dll"
run aot-child dotnet "$out/host/SkiaHost.dll" --exec "$out/aot/skia-gl"
run aot-host "$out/host-aot/SkiaHost"
for form in jit-in-process jit-child aot-child aot-host; do diff -u "$out/native.txt" "$out/$form.txt"; done
cat "$out/times.txt"
printf 'PASS: Skia GL through the gles extension draws the native frames into the host'"'"'s window (in process, JIT and NativeAOT children, one NativeAOT executable)\n'

if [[ -n "$bundle" ]]; then
    rm -rf "$bundle"
    mkdir -p "$bundle/src"
    cp -r "$out/host" "$bundle/host"
    cp -r "$out/jit" "$bundle/jit"
    cp -r "$fixture/host" "$fixture/host-aot" "$bundle/src/"
    rm -rf "$bundle/src/host/bin" "$bundle/src/host/obj" "$bundle/src/host-aot/bin" "$bundle/src/host-aot/obj"
    cp "$fixture/run.sh" "$bundle/run.sh"
    cp "$root/global.json" "$bundle/"
    tar czf "$bundle.tar.gz" -C "$(dirname "$bundle")" "$(basename "$bundle")"
    printf 'Bundle for Windows and macOS: %s.tar.gz (unpack it there and run bash %s/run.sh)\n' "$bundle" "$(basename "$bundle")"
fi
