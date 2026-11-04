#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${FFMPEG_SOURCE:-$("$root/scripts/fetch-sources.sh" --path ffmpeg)}"
runtime="${LLVMNET_RUNTIME:-portable}"
case "$runtime" in
    system|portable) suffix="-$runtime" ;;
    *) printf 'Unsupported LLVMNET_RUNTIME: %s (portable or system)\n' "$runtime" >&2; exit 2 ;;
esac
prefix="${LLVMNET_DEPS_PREFIX:-$root/artifacts/deps$suffix}"
simd128="${FFMPEG_SIMD128:-0}"
# FFMPEG_X86ASM=1: ffmpeg's nasm kernels in a native kernel package (docs/user/native-kernels.md, SV-13), which nasm
# assembles into the libraries' archives beside the C bitcode; its CPU detection selects them at run time.
x86asm="${FFMPEG_X86ASM:-0}"
simd_flags=()
architecture=(--arch=x86_64 --disable-asm --disable-x86asm --disable-runtime-cpudetect)
case "$simd128" in
    0) ;;
    1) suffix+=-simd128; simd_flags=(--simd128); architecture=(--arch=wasm --enable-asm --enable-simd128 --disable-x86asm --disable-runtime-cpudetect) ;;
    *) printf 'FFMPEG_SIMD128 must be 0 or 1\n' >&2; exit 2 ;;
esac
case "$x86asm" in
    0) ;;
    1)
        if [[ "$simd128" == 1 || "$runtime" != portable ]]; then
            printf 'FFMPEG_X86ASM=1 is a portable-mode profile without SIMD128 (system mode links native code with --system-library)\n' >&2
            exit 2
        fi
        command -v nasm >/dev/null || { printf 'FFMPEG_X86ASM=1 needs nasm\n' >&2; exit 1; }
        suffix+=-x86asm; architecture=(--arch=x86_64 --enable-x86asm --x86asmexe=nasm) ;;
    *) printf 'FFMPEG_X86ASM must be 0 or 1\n' >&2; exit 2 ;;
esac
build_dir="${FFMPEG_BUILD_DIR:-$root/artifacts/ffmpeg-browser$suffix}"
source "$root/config/ffmpeg-browser.sh"
if [[ -f "$build_dir/llvmnet-simd128" && "$(<"$build_dir/llvmnet-simd128")" != "$simd128" ]]; then
    printf 'Use separate scalar and SIMD128 ffmpeg build directories.\n' >&2
    exit 1
fi
if [[ "$(cat "$build_dir/llvmnet-x86asm" 2>/dev/null || echo 0)" != "$x86asm" && -f "$build_dir/ffbuild/config.mak" ]]; then
    printf 'Use separate ffmpeg build directories with and without FFMPEG_X86ASM.\n' >&2
    exit 1
fi
for directory in "$build_dir" "$prefix"; do
    if [[ -f "$directory/llvmnet-runtime" && "$(<"$directory/llvmnet-runtime")" != "$runtime" ]]; then
        printf 'Directory %s belongs to a different ABI; use a separate directory.\n' "$directory" >&2
        exit 1
    fi
done
if [[ -f "$source_dir/ffbuild/config.mak" ]]; then
    printf 'Use a clean FFmpeg source checkout; this script does not modify source files.\n' >&2
    exit 1
fi
for archive in libdav1d.a libxml2.a libz.a; do
    if [[ ! -f "$prefix/lib/$archive" ]]; then
        printf 'Missing %s; run scripts/build-dependencies.sh first.\n' "$archive" >&2
        exit 1
    fi
done
bash "$root/scripts/ensure-built.sh"
# Objects built against another portable sysroot carry an incompatible ABI, and not every object depends on
# config.h, so a changed sysroot means a fresh build directory.
abi_stamp="$runtime"
[[ "$runtime" == portable ]] && abi_stamp+=" $(cat "$root/sysroot/dotnet64-v2/.llvmnet-headers")"
if [[ -d "$build_dir" && "$(cat "$build_dir/llvmnet-abi" 2>/dev/null)" != "$abi_stamp" ]]; then rm -rf "${build_dir:?}"; fi
mkdir -p "$build_dir"
printf '%s\n' "$abi_stamp" >"$build_dir/llvmnet-abi"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
if [[ ! -f "$build_dir/ffbuild/config.mak" || "$(cat "$build_dir/llvmnet-source" 2>/dev/null)" != "$source_dir" || "$root/config/ffmpeg-browser.sh" -nt "$build_dir/ffbuild/config.mak" || "${BASH_SOURCE[0]}" -nt "$build_dir/ffbuild/config.mak" || "$root/config/ffmpeg-configure.patch" -nt "$build_dir/ffbuild/config.mak" ]]; then
    configure="$source_dir/configure"
    if [[ "$simd128" == 1 && "$runtime" == portable ]]; then
        mkdir -p "$build_dir/ffbuild"
        ln -sfn "$source_dir" "$build_dir/src"
        configure="$build_dir/ffbuild/llvmnet-configure"
        cp "$source_dir/configure" "$configure"
        patch --quiet "$configure" "$root/config/ffmpeg-configure.patch"
    fi
    (
        cd "$build_dir"
        "$configure" \
            --cc="$root/bin/llvmnet" --cxx="$root/bin/llvmnet" --host-cc=clang-22 \
            --ar=llvm-ar-22 --ranlib=llvm-ranlib-22 --nm=llvm-nm-22 \
            --target-os=none "${architecture[@]}" --cpu=generic --enable-cross-compile \
            --enable-static --disable-shared --disable-programs --disable-debug --disable-doc \
            --disable-inline-asm --disable-hwaccels \
            --disable-autodetect --disable-network \
            --enable-pthreads --disable-w32threads --disable-os2threads \
            --disable-filters --disable-devices --disable-protocols --enable-protocol=file \
            --disable-bsfs --disable-muxers --disable-demuxers --disable-parsers \
            --disable-decoders --disable-encoders --enable-swscale --enable-avfilter --enable-avdevice \
            --pkg-config-flags=--static --enable-zlib --enable-libxml2 --enable-libdav1d \
                --disable-stripping --extra-cflags="--runtime=$runtime ${simd_flags[*]} -O1 -fno-vectorize -fno-slp-vectorize -I$prefix/include" \
                --extra-ldflags="--runtime=$runtime ${simd_flags[*]} -L$prefix/lib" "${ffmpeg_components[@]}"
    )
            # libavcodec/msmpeg4.c guards its inline assembly by HAVE_EBX_AVAILABLE alone, which configure's probe sets
            # because llvmnet -c accepts inline assembly that it translates only at the final link.
            if [[ "$x86asm" == 1 ]]; then sed -i 's/^#define HAVE_EBX_AVAILABLE 1$/#define HAVE_EBX_AVAILABLE 0/' "$build_dir/config.h"; fi
            printf '%s\n' "$runtime" >"$build_dir/llvmnet-runtime"
            printf '%s\n' "$source_dir" >"$build_dir/llvmnet-source"
            printf '%s\n' "$simd128" >"$build_dir/llvmnet-simd128"
            printf '%s\n' "$x86asm" >"$build_dir/llvmnet-x86asm"
fi
for feature in CONFIG_ZLIB CONFIG_LIBXML2 HAVE_PTHREADS; do
    grep -q "^#define $feature 1$" "$build_dir/config.h"
done
grep -q '^#define CONFIG_LIBDAV1D_DECODER 1$' "$build_dir/config_components.h"
grep -q "^#define HAVE_SIMD128 $simd128$" "$build_dir/config.h"
if [[ "$simd128" == 1 ]]; then grep -q '^#define ARCH_WASM 1$' "$build_dir/config.h"; fi
if [[ "$x86asm" == 1 ]]; then
    grep -q '^#define HAVE_X86ASM 1$' "$build_dir/config.h"
    grep -q '^#define CONFIG_RUNTIME_CPUDETECT 1$' "$build_dir/config.h"
    grep -q '^#define HAVE_EBX_AVAILABLE 0$' "$build_dir/config.h"
fi
for feature in HAVE_INLINE_ASM CONFIG_NETWORK; do
    grep -q "^#define $feature 0$" "$build_dir/config.h"
done
if [[ "${1:-}" == --configure-only ]]; then exit 0; fi
make -C "$build_dir" -j"${JOBS:-8}"
if [[ "${1:-}" == --objects-only ]]; then exit 0; fi
archives=(
    "$build_dir/libavformat/libavformat.a" "$build_dir/libavcodec/libavcodec.a"
    "$build_dir/libavdevice/libavdevice.a" "$build_dir/libavfilter/libavfilter.a"
    "$build_dir/libavutil/libavutil.a" "$build_dir/libswresample/libswresample.a" "$build_dir/libswscale/libswscale.a"
    "$prefix/lib/libdav1d.a" "$prefix/lib/libxml2.a" "$prefix/lib/libz.a"
)
kernel_flags=()
cpu_detection=()
if [[ "$x86asm" == 1 ]]; then
    # The package: the archives' native members (the nasm kernels) except the CPU detection, which is C here, and the
    # data the kernels share with the C. llvmnet links the archives' bitcode and leaves their native members to it. The C
    # calls ff_emms_asm, which clears MMX state for the x87 code of later kernels, whatever the CPU: optional, it does
    # nothing on a host without the library.
    rm -rf "$build_dir/kernels"
    python3 "$root/scripts/kernels.py" --llvmnet "$root/bin/llvmnet" --name ffmpeg-x86 --version "$(basename "$source_dir" | sed 's/^ffmpeg-//')" \
        --library ffmpeg-x86-kernels --output "$build_dir/kernels" --runtime linux-x64 "${archives[@]:0:7}" \
        --bitcode "${archives[@]:0:7}" --exclude ff_cpu_cpuid ff_cpu_xgetbv --optional ff_emms_asm
    kernel_flags=(--native-kernels "$build_dir/kernels")
    cpu_detection=("$root/config/ffmpeg-x86-cpuid.c")
fi
"$root/bin/llvmnet" --runtime="$runtime" "${simd_flags[@]}" -pthread -O1 -I"$build_dir" -I"$source_dir" "$root/tests/ffmpeg/decode.c" \
    "${cpu_detection[@]}" "${kernel_flags[@]}" "${archives[@]}" -lm --keep-ir "$build_dir/decode.bc" -o "$build_dir/decode.dll"
if [[ "$runtime" != portable && "$simd128" == 0 ]]; then
    clang-22 -O1 "$build_dir/decode.bc" -lm -pthread -o "$build_dir/decode-native"
fi
printf 'Built browser-profile threaded ffmpeg decoder libraries (%s ABI, SIMD128=%s, X86ASM=%s) in %s\n' "$runtime" "$simd128" "$x86asm" "$build_dir"