#include <stdint.h>

struct record { unsigned char tag; int value; double scale; };
static struct record records[] = {{3, 17, 2.5}, {7, -4, 0.5}};
static struct record *selected = &records[1];
static const int coefficients[] = {1, -2, 3, 7};
static volatile int input = 11;

__attribute__((noinline)) static int choose(int value)
{
    switch (value) {
    case 0: return 13;
    case 1: return 21;
    case 3: return 31;
    case 9: return 44;
    default: return value * 2;
    }
}

__attribute__((noinline)) static int loop(int count)
{
    int left = 1, right = 2;
    for (int index = 0; index < count; ++index) {
        int previous = left;
        left = right;
        right = previous + right;
    }
    return left;
}

__attribute__((noinline)) static struct record transform(struct record item)
{
    item.value += item.tag;
    item.scale *= 2;
    return item;
}

int main(void)
{
    int values[4];
    for (int index = 0; index < 4; ++index)
        values[index] = coefficients[index] * input;
    int (*volatile indirect)(int) = choose;
    struct record transformed = transform(records[0]);
    if (loop(input) != 233 || indirect(3) != 31 || choose(5) != 10)
        return 1;
    if (values[3] != 77 || selected->value != -4 || transformed.value != 20 || transformed.scale != 5)
        return 2;
    uint64_t high = (uint64_t)(uint32_t)(input - 12) << 32;
    if (high != UINT64_C(0xffffffff00000000) || (high >> 60) != 15)
        return 3;
    int8_t negative = (int8_t)(input - 139);
    if ((negative >> 3) != -16 || (int64_t)negative != -128 || (uint8_t)negative != 128)
        return 4;
    double real = (double)(int64_t)-input / 4.0;
    if (!(real < 0) || real != -2.75)
        return 5;
    return 0;
}