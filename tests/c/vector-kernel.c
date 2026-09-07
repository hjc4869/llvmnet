#include <stdint.h>

__attribute__((noinline)) int64_t vector_kernel(const int32_t *restrict input, int32_t *restrict output, int count)
{
    int64_t total = 0;
    for (int index = 0; index < count; index++) {
        int32_t value = input[index] * 3 + 9;
        output[index] = value < 0 ? -value : value;
        total += output[index];
    }
    return total;
}

__attribute__((noinline)) void vector_float(const float *restrict input, float *restrict output, int count)
{
    for (int index = 0; index < count; index++)
        output[index] = input[index] * 2.0f - 3.0f;
}

__attribute__((noinline)) void vector_narrow(const uint16_t *restrict input, uint8_t *restrict output, int count)
{
    for (int index = 0; index < count; index++)
        output[index] = (uint8_t)(input[index] > 255 ? 255 : input[index]);
}