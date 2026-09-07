#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "libavutil/cpu.h"
#include "libavcodec/hevc/dsp.h"
#include "libavcodec/wasm/hevc/idct.h"

int main(void)
{
    HEVCDSPContext scalar, simd;
    uint32_t seed = 128;
    _Alignas(32) int16_t reference[1024], actual[1024];
    for (int depth = 8; depth <= 10; depth += 2) {
        av_force_cpu_flags(0);
        ff_hevc_dsp_init(&scalar, depth);
        av_force_cpu_flags(AV_CPU_FLAG_SIMD128);
        ff_hevc_dsp_init(&simd, depth);
        if (simd.idct[0] != (depth == 8 ? ff_hevc_idct_4x4_8_simd128 : ff_hevc_idct_4x4_10_simd128))
            return 1;
        for (int block = 0; block < 4; block++) {
            if (scalar.idct[block] == simd.idct[block])
                return 2;
            int size = 4 << block;
            for (int trial = 0; trial < 64; trial++) {
                for (int index = 0; index < size * size; index++) {
                    seed = seed * 1664525u + 1013904223u;
                    reference[index] = trial == 0 ? 0 : (int16_t)(seed >> 16);
                }
                memcpy(actual, reference, size * size * sizeof(int16_t));
                scalar.idct[block](reference, size);
                simd.idct[block](actual, size);
                if (memcmp(reference, actual, size * size * sizeof(int16_t))) {
                    printf("IDCT mismatch: depth=%d size=%d trial=%d\n", depth, size, trial);
                    return 3;
                }
            }
        }
    }
    av_force_cpu_flags(0);
    ff_hevc_dsp_init(&scalar, 8);
    av_force_cpu_flags(AV_CPU_FLAG_SIMD128);
    ff_hevc_dsp_init(&simd, 8);
    uint8_t source[66 * 192], expected[66 * 192], output[66 * 192];
    const int16_t offsets[8] = {0, -7, -3, 6, 7, 0, 0, 0};
    for (int index = 0; index < (int)sizeof(source); index++) {
        seed = seed * 1664525u + 1013904223u;
        source[index] = (uint8_t)(seed >> 24);
    }
    for (int block = 0; block < 4; block++) {
        int width = 8 << block;
        if (scalar.sao_band_filter[block] == simd.sao_band_filter[block] || scalar.sao_edge_filter[block] == simd.sao_edge_filter[block])
            return 4;
        for (int band = 0; band < 32; band++) {
            memset(expected, 0xa5, sizeof(expected));
            memset(output, 0xa5, sizeof(output));
            scalar.sao_band_filter[block](expected + 208, source + 208, 192, 192, offsets, band, width, 8);
            simd.sao_band_filter[block](output + 208, source + 208, 192, 192, offsets, band, width, 8);
            if (memcmp(expected, output, sizeof(output)))
                return 5;
        }
        for (int direction = 0; direction < 4; direction++) {
            memset(expected, 0xa5, sizeof(expected));
            memset(output, 0xa5, sizeof(output));
            scalar.sao_edge_filter[block](expected + 208, source + 208, 192, offsets, direction, width, 8);
            simd.sao_edge_filter[block](output + 208, source + 208, 192, offsets, direction, width, 8);
            if (memcmp(expected, output, sizeof(output)))
                return 6;
        }
    }
    puts("PASS: SIMD128 dispatch, 512 IDCT cases, 144 SAO cases and output boundaries");
    return 0;
}