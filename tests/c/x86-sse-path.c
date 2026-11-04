/* A library-style SIMD switch: the SSE2 path when __SSE2__ is defined, the portable C path otherwise. Native x86-64,
   llvmnet's system mode and --isa=x86-64-v2 define __SSE2__; the SSE2 path uses psadbw, an x86 target intrinsic. */
#include <stdio.h>
#ifdef __SSE2__
#include <emmintrin.h>
#endif

static int sad16(const unsigned char *a, const unsigned char *b)
{
#ifdef __SSE2__
    __m128i sums = _mm_sad_epu8(_mm_loadu_si128((const __m128i *)a), _mm_loadu_si128((const __m128i *)b));
    return _mm_cvtsi128_si32(sums) + _mm_extract_epi16(sums, 4);
#else
    int sum = 0;
    for (int i = 0; i < 16; i++) sum += a[i] > b[i] ? a[i] - b[i] : b[i] - a[i];
    return sum;
#endif
}

int main(void)
{
    unsigned char a[16], b[16];
    for (int i = 0; i < 16; i++) {
        a[i] = (unsigned char)(i * 17);
        b[i] = (unsigned char)(255 - i * 9);
    }
    printf("sad=%d\n", sad16(a, b));
    return 0;
}
