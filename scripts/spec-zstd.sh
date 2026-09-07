#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
media="${SPEC2026_MEDIA:-/mnt/cpu2026}"
stage="$root/artifacts/spec2026"
benchmark="$stage/benchspec/CPU/777.zstd_r"
if [[ ! -f "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" ]]; then
    printf 'Complete the ffmpeg full-video validation first.\n' >&2
    exit 1
fi
diff -q "$root/artifacts/ffmpeg-browser/video-validation/native-frames.txt" "$root/artifacts/ffmpeg-browser/video-validation/cil-frames.txt" >/dev/null
mkdir -p "$stage"
if [[ ! -d "$benchmark/src" ]]; then
    archives=("$media"/install_archives/benchball/777.zstd_r-*.tar.xz)
    if [[ ${#archives[@]} -ne 1 || ! -f "${archives[0]}" ]]; then
        printf 'Expected one zstd benchmark archive in %s\n' "$media" >&2
        exit 1
    fi
    tar --no-same-owner -xJf "${archives[0]}" -C "$stage"
fi
source_dir="$benchmark/src/zstd-1.5.6"
build="$stage/llvmnet-zstd"
mkdir -p "$build"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
sources=(
    programs/fileio.c programs/fileio_asyncio.c programs/util.c programs/timefn.c programs/zstdcli.c
    programs/benchzstd.c programs/datagen.c programs/benchfn.c programs/lorem.c
    lib/compress/zstd_double_fast.c lib/compress/zstd_fast.c lib/compress/zstd_compress_sequences.c
    lib/compress/zstd_ldm.c lib/compress/hist.c lib/compress/zstd_compress.c lib/compress/zstd_compress_superblock.c
    lib/compress/zstd_lazy.c lib/compress/zstd_compress_literals.c lib/compress/huf_compress.c lib/compress/zstd_opt.c lib/compress/fse_compress.c
    lib/decompress/zstd_ddict.c lib/decompress/huf_decompress.c lib/decompress/zstd_decompress.c lib/decompress/zstd_decompress_block.c
    lib/common/entropy_common.c lib/common/fse_decompress.c lib/common/debug.c lib/common/xxhash.c
    lib/common/pool.c lib/common/threading.c lib/common/zstd_common.c lib/common/error_private.c
)
paths=()
for file in "${sources[@]}"; do paths+=("$source_dir/$file"); done
"$root/bin/llvmnet" -O1 -DSPEC -DSPEC_CPU -DSPEC_CPU_LP64 -DSPEC_AUTO_SUPPRESS_OPENMP \
    -DXXH_NAMESPACE=ZSTD_ -DZSTD_LEGACY_SUPPORT=0 -DZSTD_NODICT -DSTATIC_BMI2=0 -DDYNAMIC_BMI2=0 \
    -DZSTD_NO_INTRINSICS=1 -DZSTD_DISABLE_ASM=1 -DZSTD_NOTRACE=1 -DZSTD_NO_TRACE=1 \
    -I"$benchmark/src/spec_inline_control" -I"$source_dir/lib" -I"$source_dir/programs" \
    -I"$source_dir/lib/common" -I"$source_dir/lib/compress" -I"$source_dir/lib/dictBuilder" \
    "${paths[@]}" --keep-ir "$build/zstd.bc" -o "$build/zstd.dll"
clang-22 -O1 "$build/zstd.bc" -pthread -lm -o "$build/zstd-native"
"$build/zstd-native" -b3 --verbose -i1 >"$build/native.out" 2>"$build/native.err"
dotnet "$build/zstd.dll" -b3 --verbose -i1 >"$build/cil.out" 2>"$build/cil.err"
printf 'PASS: CPU 2026 777.zstd_r test workload, one process, native and CIL completed compression/decompression checks. Development run, not a SPEC score.\n'