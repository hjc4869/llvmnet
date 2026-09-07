#include <stdint.h>

int64_t vector_kernel(const int32_t *restrict, int32_t *restrict, int);
void vector_float(const float *restrict, float *restrict, int);
void vector_narrow(const uint16_t *restrict, uint8_t *restrict, int);

int main(void)
{
    int32_t input[131], output[131];
    float floats[131], float_output[131];
    uint16_t wide[131];
    uint8_t narrow[131];
    for (int index = 0; index < 131; index++) {
        input[index] = index % 17 - 8;
        floats[index] = (float)index * 0.5f;
        wide[index] = (uint16_t)(index * 7);
    }
    int64_t actual = vector_kernel(input, output, 131);
    vector_float(floats, float_output, 131);
    vector_narrow(wide, narrow, 131);
    int64_t expected = 0;
    for (int index = 0; index < 131; index++) {
        int32_t value = (index % 17 - 8) * 3 + 9;
        if (value < 0) value = -value;
        if (output[index] != value) return 1;
        if (float_output[index] != (float)index - 3.0f) return 2;
        if (narrow[index] != (index * 7 > 255 ? 255 : index * 7)) return 3;
        expected += value;
    }
    return actual == expected ? 0 : 4;
}