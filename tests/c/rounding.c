#include <fenv.h>
#include <stdio.h>

int main(void)
{
    int original = fegetround();
    int modes[] = {FE_TONEAREST, FE_TOWARDZERO, FE_UPWARD, FE_DOWNWARD};
    int expected[] = {1, 0, 2, 3};
    int failure = 0;
    for (int index = 0; index < 4; ++index) {
        if (fesetround(modes[index]) || __builtin_flt_rounds() != expected[index])
            failure = 1;
    }
    if (fesetround(original))
        return 2;
    if (failure)
        return 1;
    puts("rounding modes reported correctly");
    return 0;
}